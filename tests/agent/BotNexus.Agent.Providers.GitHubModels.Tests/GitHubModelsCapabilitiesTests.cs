using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.GitHubModels;
using Shouldly;

namespace BotNexus.Agent.Providers.GitHubModels.Tests;

/// <summary>
/// Issue #2432: GitHub Models is one of the five providers required to surface
/// <c>ProviderCapabilities</c>, but it is the one that does so INDIRECTLY, and that indirection is
/// exactly what these tests pin.
/// <para>
/// <c>GitHubModelsProvider</c> is not an <c>IApiProvider</c> at all -- it is a model-catalog
/// registrar that registers every model with <c>Api: "openai-compat"</c> so the existing
/// OpenAI-compatible provider serves the requests. Its capability declaration is therefore
/// <c>OpenAICompatProvider</c>'s declaration, and the way that stays true is for every GitHub
/// Models model to keep routing there. A future change that gave GitHub Models its own api id
/// would silently strand it on the interface default, so the routing assertion IS the capability
/// assertion.
/// </para>
/// </summary>
public sealed class GitHubModelsCapabilitiesTests
{
    /// <summary>
    /// Every registered GitHub Models model routes to the <c>openai-compat</c> api, which is what
    /// makes <c>OpenAICompatProvider.Capabilities</c> the declaration GitHub Models surfaces.
    /// Asserting over the whole catalog rather than one sample means a single unrouted model added
    /// later fails here rather than in production.
    /// </summary>
    [Fact]
    public void EveryRegisteredModel_RoutesToTheOpenAiCompatProvider()
    {
        var registry = new ModelRegistry();

        GitHubModelsProvider.RegisterModels(registry);

        var models = registry.GetModels(GitHubModelsProvider.ProviderName);
        models.ShouldNotBeEmpty("GitHub Models must register a catalog for its capability declaration to mean anything.");
        foreach (var model in models)
        {
            model.Api.ShouldBe(
                "openai-compat",
                $"GitHub Models model '{model.Id}' must route to openai-compat so it inherits that " +
                "provider's declared ProviderCapabilities (#2432).");
        }
    }
}
