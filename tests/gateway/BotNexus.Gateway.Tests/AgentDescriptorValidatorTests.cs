using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests;

public sealed class AgentDescriptorValidatorTests
{
    [Fact]
    public void Validate_WithValidDescriptor_ReturnsNoErrors()
    {
        var descriptor = CreateValidDescriptor();

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldBeEmpty();
    }

    // Validate_WithoutAgentId_ReturnsAgentIdError was removed: AgentId is now a Vogen value
    // object (BotNexus.Domain.Primitives.AgentId) and cannot be constructed as default. Missing
    // / null / whitespace agent IDs are rejected at construction time; see AgentIdTests.From_RejectsNullEmptyOrWhitespace.

    [Fact]
    public void Validate_WithoutDisplayName_ReturnsDisplayNameError()
    {
        var descriptor = CreateValidDescriptor() with { DisplayName = string.Empty };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("DisplayName is required.");
    }

    [Fact]
    public void Validate_WithoutModelId_ReturnsModelIdError()
    {
        var descriptor = CreateValidDescriptor() with { ModelId = string.Empty };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("ModelId is required.");
    }

    [Fact]
    public void Validate_WithoutApiProvider_ReturnsApiProviderError()
    {
        var descriptor = CreateValidDescriptor() with { ApiProvider = string.Empty };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("ApiProvider is required.");
    }

    [Fact]
    public void Validate_WithSystemPromptAndSystemPromptFile_ReturnsNoPromptErrors()
    {
        var descriptor = CreateValidDescriptor() with
        {
            SystemPrompt = "Prompt",
            SystemPromptFile = "prompt.txt"
        };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldNotContain(error => error.Contains("SystemPrompt", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithoutSystemPromptAndSystemPromptFile_ReturnsNoPromptErrors()
    {
        var descriptor = CreateValidDescriptor() with
        {
            SystemPrompt = null,
            SystemPromptFile = null
        };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldNotContain(error => error.Contains("SystemPrompt", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithNegativeMaxConcurrentSessions_ReturnsError()
    {
        var descriptor = CreateValidDescriptor() with { MaxConcurrentSessions = -1 };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("MaxConcurrentSessions must be >= 0.");
    }

    [Fact]
    public void Validate_WithZeroMaxConcurrentSessions_ReturnsNoErrors()
    {
        var descriptor = CreateValidDescriptor() with { MaxConcurrentSessions = 0 };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldBeEmpty();
    }

    // ---- ValidateForConfig (Phase 5 / F-6 — config & REST guard for AgentKind.SubAgent) ----

    [Fact]
    public void ValidateForConfig_WithKindNamed_DefaultKind_ReturnsNoKindError()
    {
        // Baseline: default Kind (Named) on a valid descriptor must produce no Kind-related error.
        var descriptor = CreateValidDescriptor();

        var errors = AgentDescriptorValidator.ValidateForConfig(descriptor);

        errors.ShouldNotContain(error => error.Contains("Kind", StringComparison.Ordinal),
            "ValidateForConfig must allow Kind = Named (the default). Errors received: " +
            string.Join("; ", errors));
    }

    [Fact]
    public void ValidateForConfig_WithKindSubAgent_ReturnsKindError()
    {
        // Security guard: a sub-agent must NEVER be declared from config / REST. Only
        // DefaultSubAgentManager.SpawnAsync is allowed to stamp Kind = SubAgent on a descriptor.
        // If we allowed this from config, an attacker who could write to the agent config could
        // either (a) bypass the spawn-tool deny gate (Named privilege under the guise of a
        // SubAgent registration) or (b) silently deprive a legitimate named agent of
        // spawn_subagent on every session.
        var descriptor = CreateValidDescriptor() with { Kind = BotNexus.Domain.World.AgentKind.SubAgent };

        var errors = AgentDescriptorValidator.ValidateForConfig(descriptor);

        errors.ShouldContain(error => error.Contains("Kind = SubAgent is reserved", StringComparison.Ordinal),
            "ValidateForConfig must reject Kind = SubAgent. Errors received: " +
            string.Join("; ", errors));
    }

    [Fact]
    public void Validate_WithKindSubAgent_DoesNotReturnKindError()
    {
        // Runtime path contract: the spawn-time supervisor lookup uses Validate (not
        // ValidateForConfig), so Validate MUST accept Kind = SubAgent. If this assertion
        // regresses, every sub-agent spawn would fail at supervisor.GetOrCreateAsync time,
        // breaking all sub-agent functionality across the platform.
        var descriptor = CreateValidDescriptor() with { Kind = BotNexus.Domain.World.AgentKind.SubAgent };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldNotContain(error => error.Contains("Kind", StringComparison.Ordinal),
            "Validate (runtime path) must allow Kind = SubAgent — used by DefaultSubAgentManager " +
            "via DefaultAgentSupervisor.GetOrCreateAsync. Errors received: " + string.Join("; ", errors));
    }

    // ---- PBI4 (#1705): agent-level thinking + context validated against model capabilities ----

    // A reasoning model that advertises extra-high thinking AND extended context, so the full
    // option set (minimal..max, 200K/1M) is available for happy-path assertions.
    private const string ReasoningModelId = "cap-model-reasoning";
    // A non-reasoning, fixed-window model, so any thinking level or non-default context is invalid.
    private const string PlainModelId = "cap-model-plain";
    private const string CapProvider = "cap-provider";

    private static LlmModel MakeModel(
        string id, bool reasoning, int contextWindow,
        bool extraHigh = false, bool extendedContext = false) =>
        new(
            Id: id,
            Name: id,
            Api: reasoning ? "messages" : "completions",
            Provider: CapProvider,
            BaseUrl: "https://example.invalid",
            Reasoning: reasoning,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: contextWindow,
            MaxTokens: 16_000,
            SupportsExtraHighThinking: extraHigh,
            SupportsExtendedContextWindow: extendedContext);

    private static ModelRegistry CreateCapabilityRegistry()
    {
        var registry = new ModelRegistry();
        registry.Register(CapProvider, MakeModel(ReasoningModelId, reasoning: true, contextWindow: 200_000, extraHigh: true, extendedContext: true));
        registry.Register(CapProvider, MakeModel(PlainModelId, reasoning: false, contextWindow: 128_000));
        return registry;
    }

    private static AgentDescriptor CapDescriptor(string modelId) =>
        CreateValidDescriptor() with { ApiProvider = CapProvider, ModelId = modelId };

    [Fact]
    public void ValidateModelCapabilities_WithSupportedThinkingAndContext_ReturnsNoErrors()
    {
        var registry = CreateCapabilityRegistry();
        var descriptor = CapDescriptor(ReasoningModelId) with { Thinking = "high", ContextWindow = 1_000_000 };

        var errors = AgentDescriptorValidator.ValidateModelCapabilities(descriptor, registry);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateModelCapabilities_WithExtraHighThinkingOnCapableModel_ReturnsNoErrors()
    {
        var registry = CreateCapabilityRegistry();
        var descriptor = CapDescriptor(ReasoningModelId) with { Thinking = "xhigh" };

        var errors = AgentDescriptorValidator.ValidateModelCapabilities(descriptor, registry);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateModelCapabilities_WithThinkingOnNonReasoningModel_ReturnsError()
    {
        var registry = CreateCapabilityRegistry();
        var descriptor = CapDescriptor(PlainModelId) with { Thinking = "high" };

        var errors = AgentDescriptorValidator.ValidateModelCapabilities(descriptor, registry);

        errors.ShouldContain(e => e.Contains("does not support thinking", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateModelCapabilities_WithUnsupportedThinkingLevel_ReturnsError()
    {
        // xhigh requires the extra-high capability; a reasoning model without it must reject it.
        var registry = new ModelRegistry();
        registry.Register(CapProvider, MakeModel("reasoning-basic", reasoning: true, contextWindow: 200_000));
        var descriptor = CapDescriptor("reasoning-basic") with { Thinking = "xhigh" };

        var errors = AgentDescriptorValidator.ValidateModelCapabilities(descriptor, registry);

        errors.ShouldContain(e => e.Contains("not supported by model", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateModelCapabilities_WithUnsupportedContextWindow_ReturnsError()
    {
        var registry = CreateCapabilityRegistry();
        // Plain model exposes only its single 128K window; 1M must be rejected.
        var descriptor = CapDescriptor(PlainModelId) with { ContextWindow = 1_000_000 };

        var errors = AgentDescriptorValidator.ValidateModelCapabilities(descriptor, registry);

        errors.ShouldContain(e => e.Contains("Context window", StringComparison.Ordinal) &&
                                  e.Contains("not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateModelCapabilities_WithMalformedThinking_ReturnsErrorEvenWithoutRegistry()
    {
        var descriptor = CreateValidDescriptor() with { Thinking = "bogus" };

        var errors = AgentDescriptorValidator.ValidateModelCapabilities(descriptor, modelRegistry: null);

        errors.ShouldContain(e => e.Contains("not a recognised thinking level", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateModelCapabilities_WithNullFields_ReturnsNoErrors()
    {
        var registry = CreateCapabilityRegistry();
        var descriptor = CapDescriptor(ReasoningModelId);

        var errors = AgentDescriptorValidator.ValidateModelCapabilities(descriptor, registry);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WithRegistry_FlowsThroughCapabilityErrors()
    {
        var registry = CreateCapabilityRegistry();
        var descriptor = CapDescriptor(PlainModelId) with { Thinking = "high" };

        var errors = AgentDescriptorValidator.Validate(descriptor, availableIsolationStrategies: null, modelRegistry: registry);

        errors.ShouldContain(e => e.Contains("does not support thinking", StringComparison.Ordinal));
    }

    private static AgentDescriptor CreateValidDescriptor()
        => new()
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "model",
            ApiProvider = "provider",
            SystemPrompt = "Prompt",
            MaxConcurrentSessions = 1
        };
}
