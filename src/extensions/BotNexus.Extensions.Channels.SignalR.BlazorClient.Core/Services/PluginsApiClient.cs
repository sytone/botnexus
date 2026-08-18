using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>Integrity state of an installed plugin's content on disk.</summary>
public enum PluginTrustStateDto
{
    /// <summary>Integrity could not be attested.</summary>
    Unverified = 0,

    /// <summary>Content matches what install recorded.</summary>
    Verified = 1,

    /// <summary>Content diverges from what install recorded.</summary>
    Modified = 2,
}

/// <summary>Whether a newer revision is available at a plugin's source.</summary>
public enum PluginUpdateStateDto
{
    /// <summary>The source was not probed.</summary>
    Unknown = 0,

    /// <summary>The source resolves to the revision already on disk.</summary>
    Current = 1,

    /// <summary>The source resolves to a newer revision.</summary>
    UpdateAvailable = 2,

    /// <summary>Updates are disabled, so the source is deliberately not probed.</summary>
    Pinned = 3,

    /// <summary>The source could not be probed.</summary>
    ProbeFailed = 4,
}

/// <summary>One installed plugin as rendered by the portal plugins page.</summary>
public sealed record PluginRowDto
{
    /// <summary>Plugin identifier; also the <c>/plugins/{PluginId}</c> route parameter.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Marketplace source the content came from.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>Branch or tag requested at install time, or <c>null</c> for the default branch.</summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    /// <summary>Exact revision currently on disk.</summary>
    [JsonPropertyName("resolvedVersion")]
    public string ResolvedVersion { get; init; } = string.Empty;

    /// <summary>Version the plugin's manifest advertises, or <c>null</c> when unversioned.</summary>
    [JsonPropertyName("manifestVersion")]
    public string? ManifestVersion { get; init; }

    /// <summary>Whether scheduled updates may replace this plugin's content.</summary>
    [JsonPropertyName("updatesEnabled")]
    public bool UpdatesEnabled { get; init; }

    /// <summary>When the content on disk was materialised.</summary>
    [JsonPropertyName("installedAtUtc")]
    public DateTimeOffset InstalledAtUtc { get; init; }

    /// <summary>Number of files install recorded.</summary>
    [JsonPropertyName("fileCount")]
    public int FileCount { get; init; }

    /// <summary>Integrity state of the content on disk.</summary>
    [JsonPropertyName("trustState")]
    public PluginTrustStateDto TrustState { get; init; }

    /// <summary>Operator-readable explanation of the trust state.</summary>
    [JsonPropertyName("trustDetail")]
    public string? TrustDetail { get; init; }

    /// <summary>Update availability at the plugin's source.</summary>
    [JsonPropertyName("updateState")]
    public PluginUpdateStateDto UpdateState { get; init; }

    /// <summary>Revision the source resolves to, when it was probed.</summary>
    [JsonPropertyName("availableVersion")]
    public string? AvailableVersion { get; init; }

    /// <summary>Why an update probe failed.</summary>
    [JsonPropertyName("updateProbeError")]
    public string? UpdateProbeError { get; init; }
}

/// <summary>Request body for toggling a plugin's auto-update preference.</summary>
public sealed record PluginUpdatePreferenceDto
{
    /// <summary>Whether scheduled updates may replace this plugin's content.</summary>
    [JsonPropertyName("updatesEnabled")]
    public bool UpdatesEnabled { get; init; }
}

/// <summary>
/// Client for the gateway plugins REST API (<c>/api/plugins</c>, #2687, slice 8 of #2623).
/// </summary>
/// <remarks>
/// The list call swallows transport failures into an empty list so the page renders its own empty
/// state rather than an error boundary. The preference write does NOT swallow: a toggle that
/// silently failed would leave the switch showing a preference the gateway never stored, which is
/// exactly the lie a persistence control must not tell.
/// </remarks>
public sealed class PluginsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>Initialises the client over the portal's configured <see cref="HttpClient"/>.</summary>
    /// <param name="http">Portal HTTP client.</param>
    public PluginsApiClient(HttpClient http) => _http = http;

    /// <summary>Lists every installed plugin, ordered by name. Never returns null.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<PluginRowDto>> ListAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PluginRowDto>>("/api/plugins", JsonOptions, ct);
        return result ?? [];
    }

    /// <summary>
    /// Sets a plugin's auto-update preference and returns the refreshed row, or <c>null</c> when
    /// the gateway refused the write.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="updatesEnabled">New preference.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PluginRowDto?> SetUpdatePreferenceAsync(
        string name,
        bool updatesEnabled,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        using var response = await _http.PutAsJsonAsync(
            $"/api/plugins/{Uri.EscapeDataString(name)}/update-preference",
            new PluginUpdatePreferenceDto { UpdatesEnabled = updatesEnabled },
            JsonOptions,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<PluginRowDto>(JsonOptions, ct);
    }
}
