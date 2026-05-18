using System.Net.Http.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Polls <c>GET /api/gateway/update/status</c> and caches the result for Blazor components.
/// Implements <see cref="IUpdateStatusService"/> to surface update state without requiring
/// each component to manage its own HTTP lifecycle.
/// </summary>
public sealed class UpdateStatusService : IUpdateStatusService
{
    private readonly HttpClient _http;
    private UpdateStatus? _status;

    /// <inheritdoc />
    public UpdateStatus? Status => _status;

    /// <inheritdoc />
    public event Action? StatusChanged;

    /// <summary>
    /// Initializes the service with an <see cref="HttpClient"/> configured with the gateway base address.
    /// </summary>
    public UpdateStatusService(HttpClient http) => _http = http;

    /// <summary>
    /// Updates the base address used for API calls. Call this once the gateway base URL is known.
    /// </summary>
    public void Configure(string apiBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
            _http.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
    }

    /// <inheritdoc />
    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _status = await _http.GetFromJsonAsync<UpdateStatus>("api/gateway/update/status", cancellationToken);
            StatusChanged?.Invoke();
        }
        catch
        {
            // Non-fatal: retain last known status on transient failure.
        }
    }

    /// <inheritdoc />
    public async Task<int> StartUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.PostAsync("api/gateway/update/start", null, cancellationToken);
            return (int)response.StatusCode;
        }
        catch
        {
            return 0;
        }
    }
}
