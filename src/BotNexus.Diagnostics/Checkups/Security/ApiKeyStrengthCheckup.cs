using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Diagnostics.Checkups.Security;

public sealed class ApiKeyStrengthCheckup(IOptions<BotNexusConfig> options) : IHealthCheckup
{
    private const int MinimumKeyLength = 16;
    private readonly BotNexusConfig _config = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Name => "ApiKeyStrength";
    public string Category => "Security";
    public string Description => "Checks configured API keys are present and sufficiently strong.";

    public Task<CheckupResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            var isDevelopment = string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Development",
                StringComparison.OrdinalIgnoreCase);

            var issues = new List<string>();
            foreach (var (providerName, provider) in _config.Providers)
            {
                var authType = provider.Auth?.Trim().ToLowerInvariant() ?? "apikey";
                if (!string.Equals(authType, "apikey", StringComparison.Ordinal))
                    continue;

                if (string.IsNullOrWhiteSpace(provider.ApiKey))
                {
                    if (!isDevelopment)
                        issues.Add($"{providerName}: ApiKey is empty in non-development environment");
                    continue;
                }

                if (provider.ApiKey.Trim().Length < MinimumKeyLength)
                    issues.Add($"{providerName}: ApiKey is shorter than {MinimumKeyLength} characters");
            }

            if (issues.Count > 0)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Warn,
                    $"API key strength issues detected: {string.Join("; ", issues)}",
                    "Use non-empty API keys of at least 16 characters for apikey providers."));
            }

            return Task.FromResult(new CheckupResult(
                CheckupStatus.Pass,
                "Configured API keys meet minimum strength checks."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckupResult(
                CheckupStatus.Fail,
                $"Failed to check API key strength: {ex.Message}",
                "Review BotNexus provider ApiKey values and environment configuration."));
        }
    }
}
