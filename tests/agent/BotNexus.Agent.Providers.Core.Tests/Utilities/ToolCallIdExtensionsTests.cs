using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

public class ToolCallIdExtensionsTests
{
    [Theory]
    [InlineData("toolu_01CBhTTz95qkd9LJMdC9sf8t", "toolu01CB")]
    [InlineData("id!", "id0000000")]
    [InlineData("!!!", "000000000")]
    [InlineData("123456789", "123456789")]
    public void NormalizeMistralToolCallId_ReturnsExactlyNineAlphanumericCharacters(
        string id, string expected)
    {
        var normalized = id.NormalizeMistralToolCallId();

        normalized.ShouldBe(expected);
        normalized.Length.ShouldBe(9);
        Regex.IsMatch(normalized, "^[a-zA-Z0-9]{9}$").ShouldBeTrue();
        id.NormalizeMistralToolCallId().ShouldBe(normalized);
    }

    [Theory]
    [InlineData("mistral", "other-model")]
    [InlineData("custom", "devstral-small-latest")]
    [InlineData("custom", "codestral-latest")]
    [InlineData("custom", "pixtral-large-latest")]
    [InlineData("custom", "open-mixtral-8x22b")]
    public void IsMistralFamily_DetectsProviderOrModelIdentity(string provider, string modelId)
    {
        MakeModel(provider, modelId).IsMistralFamily().ShouldBeTrue();
    }

    [Fact]
    public void IsMistralFamily_DoesNotMatchUnrelatedModel()
    {
        MakeModel("custom", "qwen-coder").IsMistralFamily().ShouldBeFalse();
    }

    private static LlmModel MakeModel(string provider, string modelId) => new(
        modelId, modelId, "openai-compat", provider, "https://example.test/v1", false, ["text"],
        new ModelCost(0, 0, 0, 0), 32000, 4096);
}
