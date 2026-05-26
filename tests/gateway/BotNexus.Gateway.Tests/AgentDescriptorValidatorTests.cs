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
