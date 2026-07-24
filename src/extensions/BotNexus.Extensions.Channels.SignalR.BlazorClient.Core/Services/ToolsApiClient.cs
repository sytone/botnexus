using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Client for the gateway user-defined portal tools REST API (<c>/api/tools</c>, issue #2232).
/// Tools are persisted server-side so they roam with the user across browsers and devices; this
/// client is the portal's only source of truth for the Tools nav section (#2233, slice 2 of #2231).
/// </summary>
public sealed class ToolsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>Initialises the client over the portal's configured <see cref="HttpClient"/>.</summary>
    public ToolsApiClient(HttpClient http) => _http = http;

    /// <summary>
    /// Lists all configured tools ordered by <see cref="ToolDto.Order"/> ascending. Returns an
    /// empty list (never null) on any failure so the nav renders its empty state rather than
    /// throwing.
    /// </summary>
    public async Task<IReadOnlyList<ToolDto>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<ToolDto>>("/api/tools", JsonOptions, ct);
            return result ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Wire representation of a user-defined portal tool. Mirrors the gateway's ToolDefinition record.</summary>
public sealed class ToolDto
{
    /// <summary>Stable identifier for the tool. Used to build the <c>/tools/{id}</c> nav link.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display name shown in the Tools nav section.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Target URL the tool launches. The host route lands in a later slice; only linked here.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

    /// <summary>Icon shown alongside the tool; typically a single emoji. May be empty.</summary>
    [JsonPropertyName("icon")] public string Icon { get; set; } = string.Empty;

    /// <summary>Sort order within the tool list (ascending).</summary>
    [JsonPropertyName("order")] public int Order { get; set; }
}
