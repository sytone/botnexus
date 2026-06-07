using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class ModelFamilyDetectorTests
{
    [Theory]
    [InlineData("claude-sonnet-4-20250514", ModelFamilyDetector.Claude)]
    [InlineData("claude-3-opus-20240229", ModelFamilyDetector.Claude)]
    [InlineData("claude-haiku-3.5", ModelFamilyDetector.Claude)]
    [InlineData("CLAUDE-OPUS-4", ModelFamilyDetector.Claude)]
    public void GetModelFamily_DetectsClaude(string modelId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("gpt-4o", ModelFamilyDetector.Gpt)]
    [InlineData("gpt-4o-mini", ModelFamilyDetector.Gpt)]
    [InlineData("gpt-4.1", ModelFamilyDetector.Gpt)]
    [InlineData("o1-preview", ModelFamilyDetector.Gpt)]
    [InlineData("o3-mini", ModelFamilyDetector.Gpt)]
    [InlineData("o4-mini", ModelFamilyDetector.Gpt)]
    public void GetModelFamily_DetectsGpt(string modelId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("gemini-2.5-pro", ModelFamilyDetector.Gemini)]
    [InlineData("gemini-1.5-flash", ModelFamilyDetector.Gemini)]
    public void GetModelFamily_DetectsGemini(string modelId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("copilot-chat", ModelFamilyDetector.Copilot)]
    [InlineData("github-copilot-gpt-4", ModelFamilyDetector.Copilot)]
    public void GetModelFamily_DetectsCopilot(string modelId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("deepseek-coder", ModelFamilyDetector.DeepSeek)]
    [InlineData("deepseek-v2", ModelFamilyDetector.DeepSeek)]
    public void GetModelFamily_DetectsDeepSeek(string modelId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("qwen-72b", ModelFamilyDetector.Qwen)]
    [InlineData("qwen2.5-coder", ModelFamilyDetector.Qwen)]
    public void GetModelFamily_DetectsQwen(string modelId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Meta-Llama-3.1-8B-Instruct", ModelFamilyDetector.Llama)]
    [InlineData("llama-3-70b", ModelFamilyDetector.Llama)]
    public void GetModelFamily_DetectsLlama(string modelId, string expected)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetModelFamily_ReturnsUnknownForNullOrWhitespace(string? modelId)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(ModelFamilyDetector.Unknown);
    }

    [Theory]
    [InlineData("some-custom-model")]
    [InlineData("phi-4")]
    [InlineData("mistral-small")]
    public void GetModelFamily_ReturnsUnknownForUnrecognized(string modelId)
    {
        ModelFamilyDetector.GetModelFamily(modelId).ShouldBe(ModelFamilyDetector.Unknown);
    }
}
