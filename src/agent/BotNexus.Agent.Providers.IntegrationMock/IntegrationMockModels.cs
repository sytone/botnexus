using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.IntegrationMock;

/// <summary>
/// Built-in model definitions for the integration-mock provider. Models registered here use
/// <c>Api = "integration-mock"</c> so the <see cref="ApiProviderRegistry"/> routes them to
/// <see cref="IntegrationMockProvider"/>.
/// </summary>
public sealed class IntegrationMockModels
{
    /// <summary>The provider name used in registry lookups and config files.</summary>
    public const string ProviderName = "integration-mock";

    /// <summary>The API identifier shared with <see cref="IntegrationMockProvider.Api"/>.</summary>
    public const string ApiName = "integration-mock";

    /// <summary>Default echo model id — always present in the registry regardless of config.</summary>
    public const string DefaultModelId = "integration-mock-echo";

    /// <summary>Register the built-in mock model. Idempotent — safe to call repeatedly.</summary>
    public void RegisterAll(ModelRegistry modelRegistry)
    {
        modelRegistry.Register(ProviderName, new LlmModel(
            Id: DefaultModelId,
            Name: "Integration Mock Echo",
            Api: ApiName,
            Provider: ProviderName,
            BaseUrl: string.Empty,
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128000,
            MaxTokens: 32000));
    }
}
