using System.Diagnostics;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// Default provider health check implementation that validates credential availability
/// and model registration without making live API calls. Providers that support
/// endpoint probing can extend this with HTTP checks in the future.
/// </summary>
public sealed class DefaultProviderHealthCheck : IProviderHealthCheck
{
    private readonly ModelRegistry _modelRegistry;
    private readonly GatewayAuthManager _authManager;
    private readonly ILogger<DefaultProviderHealthCheck> _logger;

    public DefaultProviderHealthCheck(
        ModelRegistry modelRegistry,
        GatewayAuthManager authManager,
        ILogger<DefaultProviderHealthCheck> logger)
    {
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        // Check credential availability
        bool hasCredentials;
        string? credentialError = null;
        try
        {
            var apiKey = await _authManager.GetApiKeyAsync(providerId, cancellationToken).ConfigureAwait(false);
            hasCredentials = !string.IsNullOrWhiteSpace(apiKey);
            if (!hasCredentials)
            {
                credentialError = "No API key or credential configured for this provider.";
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