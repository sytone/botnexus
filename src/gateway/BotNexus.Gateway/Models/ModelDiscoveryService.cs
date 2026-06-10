using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Models;

/// <summary>
/// Runs model discovery at startup by invoking all registered
/// <see cref="IModelDiscoveryProvider"/> instances and merging results
/// into the <see cref="ModelRegistry"/>. Discovery is best-effort:
/// failures are logged and the built-in registry remains as fallback.
/// </summary>
public sealed class ModelDiscoveryService
{
    private readonly ModelRegistry _modelRegistry;
    private readonly IEnumerable<IModelDiscoveryProvider> _discoveryProviders;
    private readonly ILogger<ModelDiscoveryService> _logger;

    /// <summary>Default timeout for each provider's discovery call.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public ModelDiscoveryService(
        ModelRegistry modelRegistry,
        IEnumerable<IModelDiscoveryProvider> discoveryProviders,
        ILogger<ModelDiscoveryService> logger)
    {
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _discoveryProviders = discoveryProviders ?? throw new ArgumentNullException(nameof(discoveryProviders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs discovery for all registered providers and merges results into the registry.
    /// Dynamic models overwrite matching built-in entries (same provider + modelId).
    /// New models not in built-in are added. Discovery failures do not remove existing entries.
    /// </summary>
    public async Task DiscoverAndRegisterAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _discoveryProviders)
        {
            await DiscoverFromProviderAsync(provider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DiscoverFromProviderAsync(IModelDiscoveryProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultTimeout);

            var models = await provider.DiscoverModelsAsync(timeoutCts.Token).ConfigureAwait(false);

            if (models is null)
            {
                _logger.LogDebug("Model discovery for provider {ProviderKey} returned null (unavailable). Using built-in models.", provider.ProviderKey);
                return;
            }

            var added = 0;
            var overwritten = 0;

            foreach (var model in models)
            {
                var existing = _modelRegistry.GetModel(provider.ProviderKey, model.Id);
                _modelRegistry.Register(provider.ProviderKey, model);

                if (existing is not null)
                    overwritten++;
                else
                    added++;
            }

            _logger.LogInformation(
                "Model discovery for provider {ProviderKey}: {Added} new, {Overwritten} updated (total {Total} discovered).",
                provider.ProviderKey, added, overwritten, models.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Model discovery for provider {ProviderKey} timed out after {Timeout}s. Using built-in models.",
                provider.ProviderKey, DefaultTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model discovery for provider {ProviderKey} failed. Using built-in models.", provider.ProviderKey);
        }
    }
}
