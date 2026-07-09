using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Resolution;
using Shouldly;

namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Unit tests pinning the pure capability validation for per-conversation overrides (PBI5,
/// issue #1706). Proves the "validated against capabilities" acceptance criterion: a thinking or
/// context override that the resolved model cannot express is rejected, while an expressible one is
/// accepted.
/// </summary>
public class ConversationOverrideValidatorTests
{
    private static LlmModel Model(bool reasoning, bool extraHigh = false, int contextWindow = 200_000)
        => new(
            Id: "test-model",
            Name: "Test Model",
            Api: "test",
            Provider: "test",
            BaseUrl: "https://example.invalid",
            Reasoning: reasoning,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: contextWindow,
            MaxTokens: 4096,
            SupportsExtraHighThinking: extraHigh);

    [Fact]
    public void ValidateThinking_RejectsOverride_WhenModelHasNoReasoning()
    {
        var result = ConversationOverrideValidator.ValidateThinking(Model(reasoning: false), ThinkingLevel.Low);

        result.IsValid.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public void ValidateThinking_AllowsStandardLevel_OnReasoningModel()
    {
        var result = ConversationOverrideValidator.ValidateThinking(Model(reasoning: true), ThinkingLevel.High);

        result.IsValid.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void ValidateThinking_RejectsExtraHigh_WhenModelDoesNotSupportIt()
    {
        var result = ConversationOverrideValidator.ValidateThinking(
            Model(reasoning: true, extraHigh: false), ThinkingLevel.ExtraHigh);

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void ValidateThinking_AllowsMax_WhenModelSupportsExtraHigh()
    {
        var result = ConversationOverrideValidator.ValidateThinking(
            Model(reasoning: true, extraHigh: true), ThinkingLevel.Max);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ValidateContextWindow_RejectsWhenExceedingModelMaximum()
    {
        var result = ConversationOverrideValidator.ValidateContextWindow(Model(reasoning: true, contextWindow: 200_000), 500_000);

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void ValidateContextWindow_RejectsNonPositive()
    {
        ConversationOverrideValidator.ValidateContextWindow(Model(reasoning: true), 0).IsValid.ShouldBeFalse();
        ConversationOverrideValidator.ValidateContextWindow(Model(reasoning: true), -1).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void ValidateContextWindow_AllowsShrinkingWithinModelMaximum()
    {
        var result = ConversationOverrideValidator.ValidateContextWindow(Model(reasoning: true, contextWindow: 200_000), 64_000);

        result.IsValid.ShouldBeTrue();
    }
}
