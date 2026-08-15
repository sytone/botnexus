using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Pins AC4 of #3104: the guidance section reads the provider identity from the prompt context and
/// therefore emits family guidance for a vanity model id served by a family-specific provider,
/// instead of silently dropping the section.
/// </summary>
public sealed class ModelGuidanceSectionProviderTests
{
    private static PromptContext Context(string? modelId, string? providerId) => new()
    {
        WorkspaceDir = "C:/workspace",
        Extensions = new Dictionary<string, object?>
        {
            [ModelGuidanceSection.ModelIdExtensionKey] = modelId,
            [ModelGuidanceSection.ProviderIdExtensionKey] = providerId
        }
    };

    [Fact]
    public void ProviderIdExtensionKey_IsProviderId()
    {
        ModelGuidanceSection.ProviderIdExtensionKey.ShouldBe("providerId");
    }

    // AC4 -- the headline case. "some-vanity-id" matches no family substring; only the provider
    // identity proves the family, and the section must be emitted with that family's guidance.
    [Fact]
    public void VanityModelId_FromFamilyProvider_EmitsGuidance()
    {
        var section = ModelGuidanceSection.Create();
        var context = Context("some-vanity-id", "anthropic");

        section.ShouldInclude(context).ShouldBeTrue(
            "a vanity model id served by a family-specific provider must still get family guidance");

        var lines = section.Build(context);
        lines.ShouldNotBeEmpty();
        lines.Any(static line => line.Contains("edit tool", StringComparison.Ordinal))
            .ShouldBeTrue("the emitted guidance must be the Claude family guidance");
    }

    [Fact]
    public void VanityModelId_FromGptProvider_EmitsGptGuidance()
    {
        var section = ModelGuidanceSection.Create();
        var context = Context("internal-preview-01", "openai");

        section.ShouldInclude(context).ShouldBeTrue();
        section.Build(context)
            .Any(static line => line.Contains("Never answer from memory", StringComparison.Ordinal))
            .ShouldBeTrue("the emitted guidance must include the verification rule GPT relies on");

        // The GPT rung must actually be REACHED, not merely coincide with the default: a Gemini-only
        // rule leaking in here would mean the ladder matched the wrong family.
        section.Build(context)
            .ShouldNotContain(static line => line.Contains("absolute paths", StringComparison.Ordinal));
    }

    [Fact]
    public void VanityModelId_FromGeminiProvider_EmitsGeminiGuidance()
    {
        var section = ModelGuidanceSection.Create();
        var context = Context("flash-preview", "google");

        section.ShouldInclude(context).ShouldBeTrue();
        section.Build(context)
            .Any(static line => line.Contains("absolute paths", StringComparison.Ordinal))
            .ShouldBeTrue("the emitted guidance must be the Gemini family guidance");
    }

    // AC5 non-vacuity at the section level: the provider path must not make every model eligible
    // for a FAMILY rung. Since #2433 the section itself is always emitted (an unrecognised model
    // gets the conservative default rung rather than nothing), so the honest form of this clause is
    // "no family-specific rule leaks in", not "the section disappears".
    [Fact]
    public void UnknownModelId_FromUnknownProvider_EmitsOnlyTheDefaultRung()
    {
        var section = ModelGuidanceSection.Create();
        var context = Context("phi-4", "huggingface");

        var lines = section.Build(context);

        lines.ShouldBe(ModelGuidanceSection.Default().Select(static rule => rule.Text!).ToList());
        lines.ShouldNotContain(static line => line.Contains("edit tool", StringComparison.Ordinal));
        lines.ShouldNotContain(static line => line.Contains("absolute paths", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownModelId_WithNoProvider_EmitsOnlyTheDefaultRung()
    {
        var section = ModelGuidanceSection.Create();
        var context = Context("phi-4", providerId: null);

        section.Build(context)
            .ShouldBe(ModelGuidanceSection.Default().Select(static rule => rule.Text!).ToList());
    }

    // AC3 at the section level: the model id keeps winning, so a claude-* model served through
    // Copilot still receives Claude guidance rather than falling through to the provider family.
    [Fact]
    public void ModelIdWinsOverProvider_AtTheSectionLevel()
    {
        var section = ModelGuidanceSection.Create();
        var context = Context("claude-sonnet-4-20250514", "github-copilot");

        section.Build(context)
            .Any(static line => line.Contains("edit tool", StringComparison.Ordinal))
            .ShouldBeTrue("the model id must still select Claude guidance when a provider also resolves");
    }
}
