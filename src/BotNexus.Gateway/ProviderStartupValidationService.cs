using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway;

/// <summary>
/// Validates provider authentication during startup by attempting lightweight API calls.
/// Broadcasts system messages for any auth failures (e.g., device auth requirements).
/// </summary>
public sealed class ProviderStartupValidationService : IHostedService
{
    private readonly ProviderRegistry _registry;
    private readonly IActivityStream _activityStream;
    private readonly ILogger<ProviderStartupValidationService> _logger;

    public ProviderStartupValidationService(
        ProviderRegistry registry,
        IActivityStream activityStream,
        ILogger<ProviderStartupValidationService> logger)
    {
        _registry = registry;
        _activityStream = activityStream;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var names = _registry.GetProviderNames();
        
        if (names.Count == 0)
        {
            _logger.LogInformation("No providers configured — skipping auth validation");
            return;
        }

        _logger.LogInformation("Validating authentication for {Count} provider(s)...", names.Count);

        foreach (var name in names)
        {
            await ValidateProviderAsync(name, cancellationToken);
        }

        _logger.LogInformation("Provider auth validation complete");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ValidateProviderAsync(string name, CancellationToken cancellationToken)
    {
        var provider = _registry.Get(name);
        if (provider is null)
        {
            _logger.LogWarning("Provider '{Name}' registered but not found in registry", name);
            return;
        }

        // Check OAuth providers for token validity first
        if (provider is IOAuthProvider oauthProvider && !oauthProvider.HasValidToken)
        {
            _logger.LogWarning("Provider '{Name}' has no valid OAuth token — device auth may be required", name);
            
            await _activityStream.PublishSystemMessageAsync(new SystemMessage(
                Type: "provider_auth_required",
                Title: $"{name} Authentication Required",
                Content: $"Provider '{name}' requires authentication. Device auth flow will be triggered on first use.",
                Data: new Dictionary<string, string>
                {
                    ["provider"] = name,
                    ["auth_type"] = "oauth"
                }), cancellationToken);
            
            // Don't attempt GetAvailableModelsAsync — it will trigger device flow which blocks startup
            return;
        }

        // For API key providers, test the connection
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var models = await provider.GetAvailableModelsAsync(cts.Token);
            
            if (models.Count > 0)
            {
                _logger.LogInformation("Provider '{Name}' validated successfully — {Count} model(s) available", 
                    name, models.Count);
            }
            else
            {
                _logger.LogWarning("Provider '{Name}' returned no models", name);
                
                await _activityStream.PublishSystemMessageAsync(new SystemMessage(
                    Type: "provider_status",
                    Title: $"{name} Status",
                    Content: $"Provider '{name}' is accessible but returned no available models.",
                    Data: new Dictionary<string, string>
                    {
                        ["provider"] = name,
                        ["status"] = "degraded"
                    }), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Provider '{Name}' validation timed out after 10 seconds", name);
            
            await _activityStream.PublishSystemMessageAsync(new SystemMessage(
                Type: "provider_status",
                Title: $"{name} Timeout",
                Content: $"Provider '{name}' validation timed out. The provider may be slow or unreachable.",
                Data: new Dictionary<string, string>
                {
                    ["provider"] = name,
                    ["status"] = "timeout"
                }), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provider '{Name}' validation failed: {Message}", name, ex.Message);
            
            await _activityStream.PublishSystemMessageAsync(new SystemMessage(
                Type: "provider_auth_failed",
                Title: $"{name} Auth Failed",
                Content: $"Provider '{name}' authentication failed: {ex.Message}",
                Data: new Dictionary<string, string>
                {
                    ["provider"] = name,
                    ["error"] = ex.GetType().Name,
                    ["message"] = ex.Message
                }), cancellationToken);
        }
    }
}
