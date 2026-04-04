using FluentAssertions;
using BotNexus.Providers.Core.Compatibility;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.OpenAICompat;

namespace BotNexus.Providers.OpenAICompat.Tests;

public class CompatDetectorTests
{
    private static LlmModel MakeModel(string provider, string baseUrl, OpenAICompletionsCompat? compat = null) => new(
        Id: "test-model",
        Name: "Test Model",
        Api: "openai-compat",
        Provider: provider,
        BaseUrl: baseUrl,
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 32000,
        Compat: compat
    );

    [Fact]
    public void Ollama_DetectedByUrl()
    {
        var model = MakeModel("custom", "http://localhost:11434/v1");
        var compat = CompatDetector.Detect(model);

        compat.SupportsStore.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeFalse();
        compat.SupportsReasoningEffort.Should().BeFalse();
        compat.MaxTokensField.Should().Be("max_tokens");
    }

    [Fact]
    public void Ollama_DetectedByProvider()
    {
        var model = MakeModel("ollama", "http://myserver:11434/v1");
        var compat = CompatDetector.Detect(model);

        compat.SupportsStore.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeFalse();
        compat.MaxTokensField.Should().Be("max_tokens");
        compat.RequiresToolResultName.Should().BeTrue();
    }

    [Fact]
    public void VLlm_DetectedByUrl()
    {
        var model = MakeModel("custom", "http://localhost:8000/v1");
        var compat = CompatDetector.Detect(model);

        compat.SupportsStore.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeFalse();
        compat.SupportsStrictMode.Should().BeFalse();
    }

    [Fact]
    public void VLlm_DetectedByProvider()
    {
        var model = MakeModel("vllm", "http://myserver:9999/v1");
        var compat = CompatDetector.Detect(model);

        compat.SupportsStore.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeFalse();
    }

    [Fact]
    public void LMStudio_DetectedByUrl()
    {
        var model = MakeModel("custom", "http://localhost:1234/v1");
        var compat = CompatDetector.Detect(model);

        compat.SupportsStore.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeFalse();
        compat.MaxTokensField.Should().Be("max_tokens");
        compat.SupportsStrictMode.Should().BeFalse();
    }

    [Fact]
    public void LMStudio_DetectedByProvider()
    {
        var model = MakeModel("lmstudio", "http://myserver:5555/v1");
        var compat = CompatDetector.Detect(model);

        compat.SupportsStore.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeFalse();
        compat.SupportsReasoningEffort.Should().BeFalse();
        compat.MaxTokensField.Should().Be("max_tokens");
    }

    [Fact]
    public void UnknownUrl_GetsDefaultCompat()
    {
        var model = MakeModel("unknown-provider", "http://my-custom-server:9090/v1");
        var compat = CompatDetector.Detect(model);

        compat.SupportsStore.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeFalse();
        compat.SupportsReasoningEffort.Should().BeFalse();
        compat.SupportsStrictMode.Should().BeFalse();
    }

    [Fact]
    public void ExplicitCompat_OverridesDetection()
    {
        var explicit_compat = new OpenAICompletionsCompat
        {
            SupportsStore = true,
            SupportsDeveloperRole = true,
            SupportsReasoningEffort = true,
            MaxTokensField = "max_completion_tokens",
            SupportsStrictMode = true,
        };

        // Model URL matches Ollama, but explicit compat should take priority
        var model = MakeModel("ollama", "http://localhost:11434/v1", explicit_compat);
        var compat = CompatDetector.Detect(model);

        compat.SupportsStore.Should().BeTrue();
        compat.SupportsDeveloperRole.Should().BeTrue();
        compat.SupportsReasoningEffort.Should().BeTrue();
        compat.MaxTokensField.Should().Be("max_completion_tokens");
        compat.SupportsStrictMode.Should().BeTrue();
    }
}
