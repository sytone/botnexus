using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.GitHubModels;
using Shouldly;

namespace BotNexus.Agent.Providers.GitHubModels.Tests;

/// <summary>
/// Unit tests for <see cref="GitHubModelsProvider"/>.
/// Validates provider identity, model catalog registration, and compat settings.
/// </summary>
public sealed class GitHubModelsProviderTests
{
    [Fact]
    public void ProviderName_IsGitHubModels()
    {
        GitHubModelsProvider.ProviderName.ShouldBe("github-models");
    }

    [Fact]
    public void BaseUrl_PointsToAzureInferenceEndpoint()
    {
        GitHubModelsProvider.BaseUrl.ShouldBe("https://models.inference.ai.azure.com");
    }

    [Fact]
    public void Compat_DoesNotSupportStore()
    {
        GitHubModelsProvider.Compat.SupportsStore.ShouldBe(false);
    }

    [Fact]
    public void Compat_DoesNotSupportDeveloperRole()
    {
        GitHubModelsProvider.Compat.SupportsDeveloperRole.ShouldBe(false);
    }

    [Fact]
    public void RegisterModels_SeedsGptFourOMini()
    {
        var registry = new ModelRegistry();
        GitHubModelsProvider.RegisterModels(registry);

        var model = registry.GetModel("github-models", "gpt-4o-mini");

        model.ShouldNotBeNull();
        model!.Provider.ShouldBe("github-models");
        model.BaseUrl.ShouldBe(GitHubModelsProvider.BaseUrl);
        model.Api.ShouldBe("openai-compat");
    }

    [Fact]
    public void RegisterModels_SeedsPhi4()
    {
        var registry = new ModelRegistry();
        GitHubModelsProvider.RegisterModels(registry);

        var model = registry.GetModel("github-models", "Phi-4");

        model.ShouldNotBeNull();
        model!.Name.ShouldBe("Phi-4");
        model.ContextWindow.ShouldBe(128000);
    }

    [Fact]
    public void RegisterModels_SeedsMetaLlama()
    {
        var registry = new ModelRegistry();
        GitHubModelsProvider.RegisterModels(registry);

        var model = registry.GetModel("github-models", "Meta-Llama-3.1-8B-Instruct");

        model.ShouldNotBeNull();
        model!.Name.ShouldContain("Llama");
    }

    [Fact]
    public void RegisterModels_RegistrationCountMatchesSpec()
    {
        var registry = new ModelRegistry();
        GitHubModelsProvider.RegisterModels(registry);

        var models = registry.GetModels("github-models");

        // At least the 5 required models from the issue spec (gpt-4o-mini, Phi-3.5-mini, Phi-4, Meta-Llama-3.1-8B, Mistral-small)
        models.Count.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void RegisterModels_AllModelsUseGitHubModelsBaseUrl()
    {
        var registry = new ModelRegistry();
        GitHubModelsProvider.RegisterModels(registry);

        var models = registry.GetModels("github-models");

        models.ShouldAllBe(m => m.BaseUrl == GitHubModelsProvider.BaseUrl);
    }

    [Fact]
    public void RegisterModels_AllModelsUseOpenAICompatApi()
    {
        // GitHub Models uses the openai-compat API -- dispatches through the existing provider
        var registry = new ModelRegistry();
        GitHubModelsProvider.RegisterModels(registry);

        var models = registry.GetModels("github-models");

        models.ShouldAllBe(m => m.Api == "openai-compat");
    }
}
