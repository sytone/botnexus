using System.Net.Sockets;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Diagnostics.Checkups.Connectivity;

public sealed class ProviderReachableCheckup(IOptions<BotNexusConfig> options) : IHealthCheckup
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly BotNexusConfig _config = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly Func<HttpClient> _httpClientFactory = () => new HttpClient { Timeout = RequestTimeout };

    public ProviderReachableCheckup(
        IOptions<BotNexusConfig> options,
        Func<HttpClient> httpClientFactory)
        : this(options)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public string Name => "ProviderReachable";
    public string Category => "Connectivity";
    public string Description => "Checks configured provider API base URLs are reachable via HTTP HEAD.";

    public async Task<CheckupResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            if (_config.Providers.Count == 0)
            {
                return new CheckupResult(
                    CheckupStatus.Warn,
                    "No providers are configured for connectivity checks.",
                    "Add providers with ApiBase URLs under BotNexus:Providers.");
            }

            using var httpClient = _httpClientFactory();
            var failures = new List<string>();
            var warnings = new List<string>();
            var successes = new List<string>();

            foreach (var (providerName, provider) in _config.Providers)
            {
                if (string.IsNullOrWhiteSpace(provider.ApiBase))
                {
                    warnings.Add($"{providerName}: ApiBase is not configured");
                    continue;
                }

                using var request = new HttpRequestMessage(HttpMethod.Head, provider.ApiBase);
                try
                {
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    successes.Add($"{providerName} ({(int)response.StatusCode})");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    warnings.Add($"{providerName}: timeout reaching {provider.ApiBase}");
                }
                catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                {
                    failures.Add($"{providerName}: connection refused to {provider.ApiBase}");
                }
                catch (HttpRequestException ex)
                {
                    failures.Add($"{providerName}: request failed ({ex.Message})");
                }
            }

            if (failures.Count > 0)
            {
                return new CheckupResult(
                    CheckupStatus.Fail,
                    $"Provider connectivity failures: {string.Join("; ", failures)}",
                    "Verify provider ApiBase URLs, DNS, firewall rules, and service availability.");
            }

            if (warnings.Count > 0)
            {
                return new CheckupResult(
                    CheckupStatus.Warn,
                    $"Provider connectivity warnings: {string.Join("; ", warnings)}",
                    "Check network latency/timeouts and ensure each provider has a valid ApiBase URL.");
            }

            return new CheckupResult(
                CheckupStatus.Pass,
                $"All checked providers are reachable: {string.Join(", ", successes)}.");
        }
        catch (Exception ex)
        {
            return new CheckupResult(
                CheckupStatus.Fail,
                $"Failed to run provider connectivity check: {ex.Message}",
                "Re-run diagnostics and inspect network connectivity to provider endpoints.");
        }
    }
}
