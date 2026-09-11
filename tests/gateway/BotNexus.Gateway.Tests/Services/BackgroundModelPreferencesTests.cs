using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Services;

namespace BotNexus.Gateway.Tests.Services;

/// <summary>
/// Pins how background work picks a model.
///
/// Titling and compaction summaries are small jobs no user waits on. Before this, titling took
/// whatever the registry yielded first — an arbitrary choice twice over, since providers sort
/// alphabetically (Anthropic first) and the registry is a concurrent dictionary with no stable
/// enumeration order. The practical result was a frontier model producing five words.
/// </summary>
public sealed class BackgroundModelPreferencesTests
{
    [Fact]
    public void PrefersACheapModelOverAFrontierOne()
    {
        // Deliberately ordered with the expensive model first, the way the registry can yield it.
        var available = new[] { Model("claude-opus-5"), Model("gpt-4.1-mini") };

        BackgroundModelPreferences.FirstAvailable(available)!.Id.ShouldBe("gpt-4.1-mini");
    }

    [Fact]
    public void FollowsThePreferenceOrderNotTheRegistryOrder()
    {
        // Both are acceptable; the earlier preference must win regardless of how they arrive.
        var forwards = new[] { Model("gpt-4.1-mini"), Model("claude-haiku-4.5") };
        var backwards = new[] { Model("claude-haiku-4.5"), Model("gpt-4.1-mini") };

        BackgroundModelPreferences.FirstAvailable(forwards)!.Id
            .ShouldBe(BackgroundModelPreferences.FirstAvailable(backwards)!.Id);
    }

    [Fact]
    public void FindsHaikuUnderEitherOfItsRegisteredIds()
    {
        // The Copilot route registers "claude-haiku-4.5"; the direct Anthropic route registers the
        // dated id. An installation with only one of them must still match.
        BackgroundModelPreferences.FirstAvailable([Model("claude-haiku-4.5")]).ShouldNotBeNull();
        BackgroundModelPreferences.FirstAvailable([Model("claude-haiku-4-5-20251001")]).ShouldNotBeNull();
    }

    [Fact]
    public void ReturnsNullWhenNothingCheapIsRegistered()
    {
        // The caller decides what to do about it; this must not invent a model or throw.
        BackgroundModelPreferences.FirstAvailable([Model("claude-opus-5"), Model("some-local-model")])
            .ShouldBeNull();
    }

    [Fact]
    public void ReturnsNullForAnEmptyRegistry()
    {
        BackgroundModelPreferences.FirstAvailable([]).ShouldBeNull();
    }

    private static LlmModel Model(string id) => new(
        Id: id,
        Name: id,
        Api: "openai-completions",
        Provider: "test",
        BaseUrl: "https://example.invalid/v1",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0.1m, 1.25m),
        ContextWindow: 128000,
        MaxTokens: 16384);
}
