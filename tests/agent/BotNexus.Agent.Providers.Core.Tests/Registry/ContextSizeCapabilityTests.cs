using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// Capability tests for per-model context-window selection. Mirrors the thinking-level
/// capability pattern (SupportsExtraHighThinking / GetSupportedThinkingLevels) so the UI
/// can offer only valid context sizes and never an invalid choice.
/// </summary>
public sealed class ContextSizeCapabilityTests
{
    private static LlmModel MakeModel(
        string id = "test-model",
        string provider = "test",
        int contextWindow = 200000,
        bool supportsExtendedContextWindow = false) => new(
        Id: id,
        Name: id,
        Api: "test-api",
        Provider: provider,
        BaseUrl: "https://example.com",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0, 0),
        ContextWindow: contextWindow,
        MaxTokens: 1024,
        SupportsExtendedContextWindow: supportsExtendedContextWindow);

    [Fact]
    public void GetSupportedContextSizes_WithoutExtendedCapability_ReturnsOnlyDefaultWindow()
    {
        var model = MakeModel(contextWindow: 200000, supportsExtendedContextWindow: false);

        var sizes = ModelRegistry.GetSupportedContextSizes(model);

        sizes.ShouldBe([200000]);
    }

    [Fact]
    public void GetSupportedContextSizes_WithoutExtendedCapability_MirrorsModelContextWindow()
    {
        var model = MakeModel(contextWindow: 160000, supportsExtendedContextWindow: false);

        var sizes = ModelRegistry.GetSupportedContextSizes(model);

        sizes.ShouldBe([160000]);
    }

    [Fact]
    public void GetSupportedContextSizes_WithExtendedCapability_OffersTwoHundredKAndOneMillion()
    {
        var model = MakeModel(contextWindow: 200000, supportsExtendedContextWindow: true);

        var sizes = ModelRegistry.GetSupportedContextSizes(model);

        sizes.ShouldContain(200000);
        sizes.ShouldContain(1000000);
        sizes.Count.ShouldBe(2);
    }

    [Fact]
    public void SupportsExtendedContext_WhenModelSupportsExtendedContextWindow_ReturnsTrue()
    {
        var model = MakeModel(supportsExtendedContextWindow: true);

        var result = ModelRegistry.SupportsExtendedContext(model);

        result.ShouldBeTrue();
    }

    [Fact]
    public void SupportsExtendedContext_WhenModelDoesNotSupportExtendedContextWindow_ReturnsFalse()
    {
        var model = MakeModel(supportsExtendedContextWindow: false);

        var result = ModelRegistry.SupportsExtendedContext(model);

        result.ShouldBeFalse();
    }

    [Fact]
    public void GetSupportedContextSizes_AlwaysIncludesTwoHundredKBaseTier()
    {
        var extended = MakeModel(supportsExtendedContextWindow: true);
        var standard = MakeModel(contextWindow: 200000, supportsExtendedContextWindow: false);

        ModelRegistry.GetSupportedContextSizes(extended).ShouldContain(200000);
        ModelRegistry.GetSupportedContextSizes(standard).ShouldContain(200000);
    }
}
