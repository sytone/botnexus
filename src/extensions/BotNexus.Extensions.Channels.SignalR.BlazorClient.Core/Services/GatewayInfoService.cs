using System.Globalization;
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

    /// <summary>Formats the shared build identity shown by both Portal clients.</summary>
    public static string FormatBuildIdentity(GatewayInfo? info)
    {
        if (info is null)
            return "Build identity unavailable";

        var timestamp = info.BuildTimestamp == default
            ? "time unknown"
            : info.BuildTimestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return $"{info.CommitShort} · {timestamp}";
    }
}

public sealed record GatewayInfo(
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    string CommitSha,
    string CommitShort,
    string Version,
    DateTimeOffset BuildTimestamp = default,
    string? DefaultAgentId = null);
