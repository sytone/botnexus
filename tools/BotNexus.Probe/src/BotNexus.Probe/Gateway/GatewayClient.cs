namespace BotNexus.Probe.Gateway;

public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public GatewayClient(string baseUrl)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/'), UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<GatewayHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/health", cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            return new GatewayHealthStatus(true, response.IsSuccessStatusCode, (int)response.StatusCode, payload);
        }
        catch (Exception exception)
        {
            return new GatewayHealthStatus(false, false, null, exception.Message);
        }
    }

    public Task<string?> GetRecentLogsAsync(int limit, CancellationToken cancellationToken = default)
        => SafeGetStringAsync($"/api/logs/recent?limit={Math.Max(limit, 1)}", cancellationToken);

    public Task<string?> GetSessionsAsync(CancellationToken cancellationToken = default)
        => SafeGetStringAsync("/api/sessions", cancellationToken);

    public Task<string?> GetAgentsAsync(CancellationToken cancellationToken = default)
        => SafeGetStringAsync("/api/agents", cancellationToken);

    private async Task<string?> SafeGetStringAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetStringAsync(path, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed record GatewayHealthStatus(
    bool Reachable,
    bool Healthy,
    int? StatusCode,
    string? Payload);
