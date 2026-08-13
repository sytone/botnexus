using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Pins the provider-identity fallback added for #3104. A model served under a vanity id by a
/// family-specific provider must still resolve its family; the model id must keep winning when
/// both resolve; and an unknown id from an unknown provider must still resolve <c>Unknown</c>.
/// </summary>
public sealed class ModelFamilyDetectorProviderTests
{
    // AC2 -- the vanity-id case the issue names: ids that contain no family substring at all,
    // served by a provider whose identity is the only evidence of the family.
    [Theory]
    [InlineData("some-vanity-id", "anthropic", ModelFamilyDetector.Claude)]
    [InlineData("internal-preview-01", "openai", ModelFamilyDetector.Gpt)]
    [InlineData("internal-preview-01", "azure-openai-responses", ModelFamilyDetector.Gpt)]
    [InlineData("flash-preview", "google", ModelFamilyDetector.Gemini)]
    [InlineData("coder-v3", "deepseek", ModelFamilyDetector.DeepSeek)]
    public void GetModelFamily_ResolvesFromProvider_WhenModelIdDoesNot(
        string modelId,
        string providerId,
        string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId, providerId).ShouldBe(expected);
    }

    // AC2 -- the Copilot branch the issue calls "close to dead": provider identity is the only
    // thing that can prove a Copilot-served model, because its ids never contain "copilot".
    [Fact]
    public void GetModelFamily_ResolvesCopilotFromProviderIdentity()
    {
        ModelFamilyDetector.GetModelFamily("some-vendor-preview", "github-copilot")
            .ShouldBe(ModelFamilyDetector.Copilot);
    }

    [Theory]
    [InlineData("github-copilot-completions")]
    [InlineData("github-copilot-messages")]
    public void GetModelFamily_ResolvesCopilotFromTransportProviderIds(string providerId)
    {
        ModelFamilyDetector.GetModelFamily("some-vendor-preview", providerId)
            .ShouldBe(ModelFamilyDetector.Copilot);
    }

    [Fact]
    public void GetModelFamily_ProviderMatchIsCaseInsensitive()
    {
        ModelFamilyDetector.GetModelFamily("some-vanity-id", "ANTHROPIC")
            .ShouldBe(ModelFamilyDetector.Claude);
    }

    // AC3 -- the model id wins when both resolve. A claude-* model served through Copilot must
    // still get Claude guidance, not Copilot.
    [Theory]
    [InlineData("claude-sonnet-4-20250514", "github-copilot", ModelFamilyDetector.Claude)]
    [InlineData("gpt-4o", "github-copilot", ModelFamilyDetector.Gpt)]
    [InlineData("gemini-2.5-pro", "openai", ModelFamilyDetector.Gemini)]
    public void GetModelFamily_ModelIdWinsOverProvider(string modelId, string providerId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId, providerId).ShouldBe(expected);
    }

    // AC5 (non-vacuity) -- the new path must not resolve everything.
    [Theory]
    [InlineData("phi-4", "huggingface")]
    [InlineData("some-custom-model", "my-local-gateway")]
    [InlineData("mistral-small", "mistral")]
    [InlineData("k2-turbo", "kimi-coding")]
    public void GetModelFamily_UnknownIdFromUnknownProvider_StaysUnknown(string modelId, string providerId)
    {
        ModelFamilyDetector.GetModelFamily(modelId, providerId).ShouldBe(ModelFamilyDetector.Unknown);
    }

    // A known provider cannot rescue a blank model id: with nothing to identify the model, the
    // detector must not invent a family. Guards the "provider resolves everything" failure mode
    // from the other direction.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetModelFamily_BlankModelId_StaysUnknownEvenWithKnownProvider(string? modelId)
    {
        ModelFamilyDetector.GetModelFamily(modelId, "anthropic").ShouldBe(ModelFamilyDetector.Unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetModelFamily_BlankProviderId_BehavesLikeIdOnly(string? providerId)
    {
        ModelFamilyDetector.GetModelFamily("some-custom-model", providerId)
            .ShouldBe(ModelFamilyDetector.Unknown);
        ModelFamilyDetector.GetModelFamily("claude-opus-4", providerId)
            .ShouldBe(ModelFamilyDetector.Claude);
    }

    // AC3 (behaviour parity) -- every existing id-only mapping is unchanged when the new optional
    // argument is omitted. Pinned here so the parity claim is a test, not an assertion in prose.
    [Theory]
    [InlineData("claude-3-opus-20240229", ModelFamilyDetector.Claude)]
    [InlineData("gpt-4o", ModelFamilyDetector.Gpt)]
    [InlineData("o3-mini", ModelFamilyDetector.Gpt)]
    [InlineData("gemini-1.5-flash", ModelFamilyDetector.Gemini)]
    [InlineData("copilot-chat", ModelFamilyDetector.Copilot)]
    [InlineData("deepseek-coder", ModelFamilyDetector.DeepSeek)]
    [InlineData("qwen2.5-coder", ModelFamilyDetector.Qwen)]
    [InlineData("llama-3-70b", ModelFamilyDetector.Llama)]
    [InlineData("phi-4", ModelFamilyDetector.Unknown)]
    public void GetModelFamily_IdOnlyOverload_IsUnchanged(string modelId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(expected);
    }
}
