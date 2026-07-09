using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// Capability tests for dynamic (user-defined / config-declared / discovered) models (PBI6,
/// issue #1707). A dynamic model must declare - or infer from its family - a valid thinking-level
/// and context-size set so the agent- and conversation-level pickers offer only valid choices and
/// reject invalid ones, exactly as they do for a built-in model.
/// </summary>
public sealed class DynamicModelCapabilitiesTests
{
    [Theory]
    [InlineData("claude-opus-4.6")]
    [InlineData("claude-sonnet-4-5-20250929")]
    [InlineData("gpt-5.2")]
    [InlineData("o3-mini")]
    [InlineData("gemini-3-pro")]
    [InlineData("grok-code-fast-1")]
    public void Infer_ReasoningFamily_MarksModelAsReasoning(string modelId)
    {
        var caps = DynamicModelCapabilities.Infer(modelId);

        caps.Reasoning.ShouldBeTrue();
    }

    [Theory]
    [InlineData("llama3.1")]
    [InlineData("mistral-small")]
    [InlineData("gpt-4o")]
    [InlineData("phi-4")]
    public void Infer_NonReasoningFamily_LeavesReasoningOff(string modelId)
    {
        var caps = DynamicModelCapabilities.Infer(modelId);

        caps.Reasoning.ShouldBeFalse();
    }

    [Theory]
    [InlineData("claude-opus-4.6")]
    [InlineData("claude-opus-4.8")]
    [InlineData("gpt-5.2")]
    [InlineData("gpt-5.4")]
    public void Infer_ExtraHighFamily_MarksExtraHighThinking(string modelId)
    {
        var caps = DynamicModelCapabilities.Infer(modelId);

        caps.SupportsExtraHighThinking.ShouldBeTrue();
    }

    [Theory]
    [InlineData("claude-opus-4.5")]
    [InlineData("gpt-5.1")]
    [InlineData("gpt-5")]
    public void Infer_NonExtraHighReasoningFamily_LeavesExtraHighOff(string modelId)
    {
        var caps = DynamicModelCapabilities.Infer(modelId);

        caps.Reasoning.ShouldBeTrue();
        caps.SupportsExtraHighThinking.ShouldBeFalse();
    }

    [Fact]
    public void Infer_DeclaredReasoning_OverridesFamilyInference()
    {
        // A local Ollama build of a reasoning-capable model the family heuristic does not know.
        var caps = DynamicModelCapabilities.Infer("my-custom-model", declaredReasoning: true);

        caps.Reasoning.ShouldBeTrue();
    }

    [Fact]
    public void Infer_DeclaredReasoningFalse_OverridesFamilyInference()
    {
        var caps = DynamicModelCapabilities.Infer("gpt-5.2", declaredReasoning: false);

        caps.Reasoning.ShouldBeFalse();
        // Extra-high is clamped off because the model is declared non-reasoning.
        caps.SupportsExtraHighThinking.ShouldBeFalse();
    }

    [Fact]
    public void Infer_ExtraHighDeclaredTrueOnNonReasoningModel_IsClampedOff()
    {
        var caps = DynamicModelCapabilities.Infer(
            "llama3.1",
            declaredReasoning: false,
            declaredExtraHighThinking: true);

        caps.SupportsExtraHighThinking.ShouldBeFalse();
    }

    [Fact]
    public void Infer_DeclaredExtendedContext_OverridesFamilyInference()
    {
        var caps = DynamicModelCapabilities.Infer("llama3.1", declaredExtendedContext: true);

        caps.SupportsExtendedContextWindow.ShouldBeTrue();
    }

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929")]
    [InlineData("claude-opus-4-5-20250929")]
    public void Infer_ExtendedContextFamily_MarksExtendedContext(string modelId)
    {
        var caps = DynamicModelCapabilities.Infer(modelId);

        caps.SupportsExtendedContextWindow.ShouldBeTrue();
    }

    [Fact]
    public void Infer_DynamicModel_SurfacesSameCapabilitiesThroughRegistry()
    {
        // End-to-end: a dynamic reasoning + extra-high model built from inferred capabilities must
        // expose the full thinking-level set (including Max) through the SAME registry helpers the
        // pickers read, so agent + conversation pickers offer only valid options.
        var caps = DynamicModelCapabilities.Infer("gpt-5.2");
        var model = new LlmModel(
            Id: "gpt-5.2",
            Name: "gpt-5.2",
            Api: "openai-completions",
            Provider: "custom",
            BaseUrl: "https://example.com",
            Reasoning: caps.Reasoning,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 200000,
            MaxTokens: 32000,
            SupportsExtraHighThinking: caps.SupportsExtraHighThinking,
            SupportsExtendedContextWindow: caps.SupportsExtendedContextWindow);

        var thinking = ModelRegistry.GetSupportedThinkingLevels(model);

        thinking.ShouldContain(ThinkingLevel.Max);
        thinking.ShouldContain(ThinkingLevel.ExtraHigh);
        thinking.Count.ShouldBe(6);
    }

    [Fact]
    public void Infer_NonReasoningDynamicModel_ExposesNoThinkingLevels()
    {
        var caps = DynamicModelCapabilities.Infer("llama3.1");
        var model = new LlmModel(
            Id: "llama3.1",
            Name: "llama3.1",
            Api: "openai-completions",
            Provider: "custom",
            BaseUrl: "https://example.com",
            Reasoning: caps.Reasoning,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128000,
            MaxTokens: 32000,
            SupportsExtraHighThinking: caps.SupportsExtraHighThinking,
            SupportsExtendedContextWindow: caps.SupportsExtendedContextWindow);

        ModelRegistry.GetSupportedThinkingLevels(model).ShouldBeEmpty();
    }
}
