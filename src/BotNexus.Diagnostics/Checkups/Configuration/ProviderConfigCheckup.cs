using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Diagnostics.Checkups.Configuration;

public sealed class ProviderConfigCheckup(
    IOptions<BotNexusConfig> options,
    IConfiguration configuration) : IHealthCheckup
{
    private readonly BotNexusConfig _config = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public string Name => "ProviderConfig";
    public string Category => "Configuration";
    public string Description => "Validates required provider auth fields by auth type.";

    public Task<CheckupResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            if (_config.Providers.Count == 0)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Warn,
                    "No providers are configured.",
                    "Configure at least one provider under BotNexus:Providers."));
            }

            var failures = new List<string>();
            foreach (var (providerName, provider) in _config.Providers)
            {
                var authType = provider.Auth?.Trim().ToLowerInvariant() ?? "apikey";
                switch (authType)
                {
                    case "apikey":
                        if (string.IsNullOrWhiteSpace(provider.ApiKey))
                            failures.Add($"{providerName} requires ApiKey for auth type 'apikey'");
                        break;
                    case "oauth":
                        var oauthClientId = _configuration[$"{BotNexusConfig.SectionName}:Providers:{providerName}:OAuthClientId"];
                        if (string.IsNullOrWhiteSpace(oauthClientId))
                            failures.Add($"{providerName} requires OAuthClientId for auth type 'oauth'");
                        break;
                    default:
                        failures.Add($"{providerName} has unsupported auth type '{provider.Auth}'");
                        break;
                }
            }

            if (failures.Count > 0)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Fail,
                    $"Provider auth configuration issues: {string.Join("; ", failures)}",
                    "Set ApiKey for apikey providers and OAuthClientId for oauth providers."));
            }

            return Task.FromResult(new CheckupResult(
                CheckupStatus.Pass,
                $"Validated provider auth configuration for {_config.Providers.Count} provider(s)."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckupResult(
                CheckupStatus.Fail,
                $"Failed to validate provider configuration: {ex.Message}",
                "Review BotNexus:Providers auth settings and required fields."));
        }
    }
}
