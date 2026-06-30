using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// Locks in the per-provider context-window selection rules:
/// Copilot caps every Claude model at a fixed 200K (no 1M toggle, no extended capability),
/// while Anthropic-direct exposes a selectable 200K/1M set on the long-context Claude models.
/// </summary>
public sealed class BuiltInModelsContextSizeTests
{
    private static ModelRegistry BuildRegistry()
    {
        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(registry);
        return registry;
    }

    [Fact]
    public void Copilot_CorrectedClaudeModels_AreTwoHundredK_NotOneMillion()
    {
        var registry = BuildRegistry();

        // These three Copilot Claude registrations previously hardcoded 1000000, which is
        // wrong per the verified finding (Copilot caps Claude at 200K). They are corrected to
        // 200000. Other Copilot Claude windows are the catalog's real per-model values and are
        // intentionally left untouched; the invariant that matters is "no 1M on Copilot".
        foreach (var id in new[] { "claude-opus-4.6", "claude-opus-4.8", "claude-sonnet-4.6" })
        {
            var model = registry.GetModel("github-copilot", id);
            model.ShouldNotBeNull();
            model!.ContextWindow.ShouldBe(200000);
        }
    }

    [Fact]
    public void Copilot_ClaudeModels_NeverOfferOneMillionContextSize()
    {
        var registry = BuildRegistry();

        var claude = registry.GetModels("github-copilot")
            .Where(model => model.Id.StartsWith("claude-", StringComparison.Ordinal))
            .ToList();

        claude.ShouldNotBeEmpty();
        foreach (var model in claude)
            ModelRegistry.GetSupportedContextSizes(model).ShouldNotContain(1000000);
    }

    [Fact]
    public void Copilot_ClaudeModels_DoNotAdvertiseExtendedContext()
    {
        var registry = BuildRegistry();

        var claude = registry.GetModels("github-copilot")
            .Where(model => model.Id.StartsWith("claude-", StringComparison.Ordinal))
            .ToList();

        claude.ShouldAllBe(model => !model.SupportsExtendedContextWindow);
    }

    [Fact]
    public void Copilot_ClaudeOpus48_IsTwoHundredK_NotOneMillion()
    {
        var registry = BuildRegistry();

        var opus = registry.GetModel("github-copilot", "claude-opus-4.8");

        opus.ShouldNotBeNull();
        opus!.ContextWindow.ShouldBe(200000);
        opus.SupportsExtendedContextWindow.ShouldBeFalse();
        ModelRegistry.GetSupportedContextSizes(opus).ShouldBe([200000]);
    }

    [Fact]
    public void AnthropicDirect_LongContextClaude_OffersSelectableTwoHundredKAndOneMillion()
    {
        var registry = BuildRegistry();

        var sonnet4 = registry.GetModel("anthropic", "claude-sonnet-4-20250514");

        sonnet4.ShouldNotBeNull();
        sonnet4!.SupportsExtendedContextWindow.ShouldBeTrue();
        var sizes = ModelRegistry.GetSupportedContextSizes(sonnet4);
        sizes.ShouldContain(200000);
        sizes.ShouldContain(1000000);
    }

    [Fact]
    public void AnthropicDirect_HaikuShortContext_OffersTwoHundredKOnly()
    {
        var registry = BuildRegistry();

        var haiku = registry.GetModel("anthropic", "claude-3-5-haiku-20241022");

        haiku.ShouldNotBeNull();
        haiku!.SupportsExtendedContextWindow.ShouldBeFalse();
        ModelRegistry.GetSupportedContextSizes(haiku).ShouldBe([200000]);
    }
}
