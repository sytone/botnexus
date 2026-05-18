using System.Net.Http.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Fetches build and runtime info from the gateway's /api/gateway/info endpoint.
/// Uses the hub-derived API base URL so it works correctly behind reverse proxies.
/// </summary>
public sealed class GatewayInfoService
{
    private readonly HttpClient _http;
    private readonly IGatewayRestClient _restClient;
    public GatewayInfo? Info { get; private set; }

    public GatewayInfoService(HttpClient http, IGatewayRestClient restClient)
    {
        _http = http;
        _restClient = restClient;
    }

    public async Task LoadAsync()
    {
        try
        {
            var baseUrl = _restClient.ApiBaseUrl;
            if (string.IsNullOrEmpty(baseUrl)) return;
            Info = await _http.GetFromJsonAsync<GatewayInfo>($"{baseUrl}gateway/info");
        }
        catch { }
    }
}

public sealed record GatewayInfo(
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    string CommitSha,
    string CommitShort,
    string Version);
