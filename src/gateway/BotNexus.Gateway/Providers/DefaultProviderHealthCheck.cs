using System.Diagnostics;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Providers;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// Dispatches provider-owned health policies when available; otherwise validates generic
/// credential availability and model registration without making live API calls.
/// </summary>
public sealed class DefaultProviderHealthCheck : IProviderHealthCheck
{
    private readonly ModelRegistry _modelRegistry;
    private readonly GatewayAuthManager _authManager;
    private readonly ILogger<DefaultProviderHealthCheck> _logger;
    private readonly ApiProviderRegistry? _apiProviders;

    public DefaultProviderHealthCheck(
        ModelRegistry modelRegistry,
        GatewayAuthManager authManager,
        ILogger<DefaultProviderHealthCheck> logger,
        ApiProviderRegistry? apiProviders = null)
    {
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiProviders = apiProviders;
    }

    /// <inheritdoc/>
    public async Task<ProviderHealthResult> CheckAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new ProviderHealthResult(
                providerId ?? string.Empty,
                ProviderHealthStatus.Unhealthy,
                0,
                DateTimeOffset.UtcNow,
                0,
                false,
                "Provider ID is required.");
        }

        var sw = Stopwatch.StartNew();

        // Check model registration
        var models = _modelRegistry.GetModels(providerId);
        var modelCount = models.Count;

        // Resolve via the registered model's wire API, not a provider-name/type heuristic.
        // Instance-owned providers must never fall back to the generic API-key resolver.
        if (modelCount > 0 && _apiProviders?.Get(models[0].Api) is IProviderHealthCheck healthCheck)
        {
            var result = await healthCheck.CheckAsync(models[0].Provider, cancellationToken).ConfigureAwait(false);
            return result with { ProviderId = providerId, ModelCount = modelCount };
        }

        // Check credential availability
        bool hasCredentials;
        string? credentialError = null;
        try
        {
            // #3281: resolve via the reason-carrying seam so that "the provider is down" and "nobody
            // configured this provider" produce different diagnostics. Both used to collapse into the
            // same "No API key or credential configured" string, which sent an operator hunting for a
            // missing setting during what was actually an upstream outage.
            var outcome = await _authManager.ResolveCredentialAsync(providerId, cancellationToken).ConfigureAwait(false);
            hasCredentials = outcome.Status == ProviderCredentialStatus.Resolved;
            if (!hasCredentials)
            {
                credentialError = outcome.IsProviderFault
                    ? $"Credential refresh failed ({outcome.FailureClass}{(outcome.StatusCode is { } code ? $", HTTP {code}" : string.Empty)}): {outcome.ErrorMessage}"
                    : "No API key or credential configured for this provider.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            hasCredentials = false;
            credentialError = $"Credential resolution failed: {ex.Message}";
            _logger.LogWarning(ex, "Health check credential resolution failed for provider {ProviderId}", providerId);
        }

        sw.Stop();

        // Determine overall status
        ProviderHealthStatus status;
        string? error = null;

        if (modelCount == 0)
        {
            status = ProviderHealthStatus.Unhealthy;
            error = "No models registered for this provider.";
        }
        else if (!hasCredentials)
        {
            status = ProviderHealthStatus.Unhealthy;
            error = credentialError;
        }
        else
        {
            status = ProviderHealthStatus.Healthy;
        }

        return new ProviderHealthResult(
            providerId,
            status,
            sw.ElapsedMilliseconds,
            DateTimeOffset.UtcNow,
            modelCount,
            hasCredentials,
            error);
    }
}