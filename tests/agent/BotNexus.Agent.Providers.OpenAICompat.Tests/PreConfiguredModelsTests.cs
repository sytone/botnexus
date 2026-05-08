using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.OpenAICompat;

namespace BotNexus.Agent.Providers.OpenAICompat.Tests;

public class PreConfiguredModelsTests
{
    [Fact]
    public void Ollama_HasCorrectDefaults()
    {
        var model = PreConfiguredModels.Ollama("llama3.1");

        model.Id.ShouldBe("llama3.1");
        model.Api.ShouldBe("openai-compat");
        model.Provider.ShouldBe("ollama");
        model.BaseUrl.ShouldBe("http://localhost:11434/v1");
        model.Cost.Input.ShouldBe(0);
        model.Cost.Output.ShouldBe(0);
        model.Compat.ShouldNotBeNull();
        model.Compat!.SupportsDeveloperRole.ShouldBeFalse();
        model.Compat.SupportsStore.ShouldBeFalse();
        model.Compat.MaxTokensField.ShouldBe("max_tokens");
        model.Compat.SupportsStrictMode.ShouldBeFalse();
        model.Compat.RequiresToolResultName.ShouldBeTrue();
    }

    [Fact]
    public void VLlm_HasCorrectDefaults()
    {
        var model = PreConfiguredModels.VLlm("mistral-7b");

        model.Id.ShouldBe("mistral-7b");
        model.Api.ShouldBe("openai-compat");
        model.Provider.ShouldBe("vllm");
        model.BaseUrl.ShouldBe("http://localhost:8000/v1");
        model.Cost.Input.ShouldBe(0);
        model.Compat.ShouldNotBeNull();
        model.Compat!.SupportsDeveloperRole.ShouldBeFalse();
        model.Compat.SupportsStore.ShouldBeFalse();
        model.Compat.SupportsStrictMode.ShouldBeFalse();
    }

    [Fact]
    public void LMStudio_HasCorrectDefaults()
    {
        var model = PreConfiguredModels.LMStudio("phi-3");

        model.Id.ShouldBe("phi-3");
        model.Api.ShouldBe("openai-compat");
        model.Provider.ShouldBe("lmstudio");
        model.BaseUrl.ShouldBe("http://localhost:1234/v1");
        model.Cost.Input.ShouldBe(0);
        model.Compat.ShouldNotBeNull();
        model.Compat!.SupportsDeveloperRole.ShouldBeFalse();
        model.Compat.MaxTokensField.ShouldBe("max_tokens");
        model.Compat.SupportsStrictMode.ShouldBeFalse();
    }

    [Fact]
    public void CustomBaseUrl_OverridesDefault()
    {
        var model = PreConfiguredModels.VLlm("mistral-7b", baseUrl: "http://gpu-server:8080/v1");

        model.BaseUrl.ShouldBe("http://gpu-server:8080/v1");
    }

    [Fact]
    public void CustomContextWindow_AndMaxTokens_Work()
    {
        var model = PreConfiguredModels.Ollama("llama3.1", contextWindow: 256000, maxTokens: 64000);

        model.ContextWindow.ShouldBe(256000);
        model.MaxTokens.ShouldBe(64000);
    }
}
