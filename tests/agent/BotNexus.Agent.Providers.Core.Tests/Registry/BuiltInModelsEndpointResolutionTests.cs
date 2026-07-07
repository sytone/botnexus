using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// Regression tests for #1639: the Copilot <c>LlmModel</c> must be born with the CORRECT host
/// (enterprise vs individual GitHub Copilot) resolved at registration time, so no consumer has to
/// patch <c>model.BaseUrl</c> afterwards. When the endpoint resolver declares an enterprise
/// endpoint the registered model reports it; otherwise it falls back to the individual host.
/// </summary>
public sealed class BuiltInModelsEndpointResolutionTests
{
    private const string EnterpriseEndpoint = "https://api.enterprise.githubcopilot.com";
    private const string IndividualEndpoint = "https://api.individual.githubcopilot.com";

    [Fact]
    public void RegisterAll_EnterpriseResolver_CopilotModelsCarryEnterpriseHost()
    {
        var registry = new ModelRegistry();
        // Resolver mirrors GatewayAuthManager.GetApiEndpoint reading auth.json's endpoint field.
        new BuiltInModels().RegisterAll(
            registry,
            provider => provider == "github-copilot" ? EnterpriseEndpoint : null);

        var models = registry.GetModels("github-copilot");
        models.ShouldNotBeEmpty();
        // Every Copilot model must be correct BY CONSTRUCTION - no consumer-side override.
        models.ShouldAllBe(m => m.BaseUrl == EnterpriseEndpoint);
    }

    [Fact]
    public void RegisterAll_NoResolver_CopilotModelsCarryIndividualHost()
    {
        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(registry);

        var models = registry.GetModels("github-copilot");
        models.ShouldNotBeEmpty();
        models.ShouldAllBe(m => m.BaseUrl == IndividualEndpoint);
    }

    [Fact]
    public void RegisterAll_NullEndpointFromResolver_FallsBackToIndividualHost()
    {
        var registry = new ModelRegistry();
        // Resolver present but returns null (individual account with no enterprise override).
        new BuiltInModels().RegisterAll(registry, _ => null);

        var models = registry.GetModels("github-copilot");
        models.ShouldNotBeEmpty();
        models.ShouldAllBe(m => m.BaseUrl == IndividualEndpoint);
    }

    [Fact]
    public void RegisterAll_WhitespaceEndpointFromResolver_FallsBackToIndividualHost()
    {
        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(registry, _ => "   ");

        var models = registry.GetModels("github-copilot");
        models.ShouldAllBe(m => m.BaseUrl == IndividualEndpoint);
    }

    [Fact]
    public void RegisterAll_EnterpriseResolver_LeavesNonCopilotModelsUntouched()
    {
        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(
            registry,
            _ => EnterpriseEndpoint);

        // The resolver only ever keys on the copilot provider; direct providers keep their hosts.
        registry.GetModels("anthropic").ShouldAllBe(m => m.BaseUrl == "https://api.anthropic.com");
        registry.GetModels("openai").ShouldAllBe(m => m.BaseUrl == "https://api.openai.com/v1");
    }
}
