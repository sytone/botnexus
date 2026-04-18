using FluentAssertions;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.OpenAICompat;

namespace BotNexus.Providers.OpenAICompat.Tests;

public class PreConfiguredModelsTests
{
    [Fact]
    public void Ollama_HasCorrectDefaults()
    {
        var model = PreConfiguredModels.Ollama("llama3.1");

        model.Id.Should().Be("llama3.1");
        model.Api.Should().Be("openai-compat");
        model.Provider.Should().Be("ollama");
        model.BaseUrl.Should().Be("http://localhost:11434/v1");
        model.Cost.Input.Should().Be(0);
        model.Cost.Output.Should().Be(0);
        model.Compat.Should().NotBeNull();
        model.Compat!.SupportsDeveloperRole.Should().BeFalse();
        model.Compat.SupportsStore.Should().BeFalse();
        model.Compat.MaxTokensField.Should().Be("max_tokens");
        model.Compat.SupportsStrictMode.Should().BeFalse();
        model.Compat.RequiresToolResultName.Should().BeTrue();
    }

    [Fact]
    public void VLlm_HasCorrectDefaults()
    {
        var model = PreConfiguredModels.VLlm("mistral-7b");

        model.Id.Should().Be("mistral-7b");
        model.Api.Should().Be("openai-compat");
        model.Provider.Should().Be("vllm");
        model.BaseUrl.Should().Be("http://localhost:8000/v1");
        model.Cost.Input.Should().Be(0);
        model.Compat.Should().NotBeNull();
        model.Compat!.SupportsDeveloperRole.Should().BeFalse();
        model.Compat.SupportsStore.Should().BeFalse();
        model.Compat.SupportsStrictMode.Should().BeFalse();
    }

    [Fact]
    public void LMStudio_HasCorrectDefaults()
    {
        var model = PreConfiguredModels.LMStudio("phi-3");

        model.Id.Should().Be("phi-3");
        model.Api.Should().Be("openai-compat");
        model.Provider.Should().Be("lmstudio");
        model.BaseUrl.Should().Be("http://localhost:1234/v1");
        model.Cost.Input.Should().Be(0);
        model.Compat.Should().NotBeNull();
        model.Compat!.SupportsDeveloperRole.Should().BeFalse();
        model.Compat.MaxTokensField.Should().Be("max_tokens");
        model.Compat.SupportsStrictMode.Should().BeFalse();
    }

    [Fact]
    public void CustomBaseUrl_OverridesDefault()
    {
        var model = PreConfiguredModels.VLlm("mistral-7b", baseUrl: "http://gpu-server:8080/v1");

        model.BaseUrl.Should().Be("http://gpu-server:8080/v1");
    }

    [Fact]
    public void CustomContextWindow_AndMaxTokens_Work()
    {
        var model = PreConfiguredModels.Ollama("llama3.1", contextWindow: 256000, maxTokens: 64000);

        model.ContextWindow.Should().Be(256000);
        model.MaxTokens.Should().Be(64000);
    }
}
