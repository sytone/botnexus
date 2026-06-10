using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// Discovers available models from a provider's API at runtime.
/// Implementations are registered in DI and invoked at startup to overlay
/// dynamic models onto the built-in model registry.
/// </summary>
public interface IModelDiscoveryProvider
{
    /// <summary>
    /// Provider key this discovery handles (e.g. "github-copilot", "anthropic").
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Discover available models from the provider API. Returns null if discovery
    /// is unavailable (no credentials, network error, provider unsupported) —
    /// caller falls back to built-in models.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of discovered models to register, or null if discovery failed or
    /// is not available for this provider.
    /// </returns>
    Task<IReadOnlyList<LlmModel>?> DiscoverModelsAsync(CancellationToken cancellationToken = default);
}
