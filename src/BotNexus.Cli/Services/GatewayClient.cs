using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace BotNexus.Cli.Services;

public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GatewayClient(string baseUrl = "http://localhost:18790", TimeSpan? timeout = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
        _ownsHttpClient = true;
    }

    public GatewayClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _ownsHttpClient = false;
    }

    public Task<JsonNode?> GetHealthAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("/health", cancellationToken);

    public Task<JsonNode?> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("/api/status", cancellationToken);

    public Task<JsonNode?> GetCronJobsAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("/api/cron/jobs", cancellationToken);

    public Task<JsonNode?> GetSessionsAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("/api/sessions", cancellationToken);

    public Task<JsonNode?> GetChannelsAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("/api/channels", cancellationToken);

    public Task<JsonNode?> GetExtensionsAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("/api/extensions", cancellationToken);

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetHealthAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<JsonNode?> GetJsonAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Gateway request timed out ({endpoint}).", ex);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            throw new InvalidOperationException("Gateway not running", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new InvalidOperationException("Gateway not running", ex);
        }
    }

    private static bool IsConnectionRefused(HttpRequestException exception)
        => exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
