using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Client for the gateway user-defined portal tools REST API (<c>/api/tools</c>, issue #2232).
/// Tools are persisted server-side so they roam with the user across browsers and devices; this
/// client is the portal's source of truth for the Tools nav section (#2233, slice 2 of #2231),
/// the read path for the iframe host route (<c>/tools/{id}</c>, #2234) and the write path for the
/// management UI (<c>/tools</c>, #2235, slice 4 of #2231).
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
    /// Raised after any successful create, update or delete. The layout subscribes to this so the
    /// Tools nav section repaints as soon as the management UI saves, rather than only on a full
    /// page reload (#2235 acceptance: "CRUD reflected in the nav after save"). Notifying through
    /// the existing client keeps the layout's dependency set unchanged.
    /// </summary>
    public event Action? Changed;

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

    /// <summary>
    /// Gets a single tool by identifier. Returns <c>null</c> when the tool does not exist or the
    /// request fails, so callers render a not-found state rather than throwing.
    /// </summary>
    public async Task<ToolDto?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        try
        {
            using var response = await _http.GetAsync($"/api/tools/{Uri.EscapeDataString(id)}", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ToolDto>(JsonOptions, ct)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a new tool. The caller supplies the identifier because the server treats tool ids as
    /// client-generated (#2232). Returns <c>null</c> on any failure so the management UI can show an
    /// inline error instead of throwing out of an event handler.
    /// </summary>
    public async Task<ToolDto?> CreateAsync(ToolDto tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        try
        {
            using var response = await _http.PostAsJsonAsync("/api/tools", tool, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var created = await response.Content.ReadFromJsonAsync<ToolDto>(JsonOptions, ct);
            Changed?.Invoke();
            return created;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Updates an existing tool in place. The server preserves the original creation timestamp and
    /// identifier, so only the editable fields matter here. Returns <c>null</c> on any failure.
    /// </summary>
    public async Task<ToolDto?> UpdateAsync(ToolDto tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Id))
            return null;

        try
        {
            using var response = await _http.PutAsJsonAsync(
                $"/api/tools/{Uri.EscapeDataString(tool.Id)}", tool, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var updated = await response.Content.ReadFromJsonAsync<ToolDto>(JsonOptions, ct);
            Changed?.Invoke();
            return updated;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a tool. Returns <c>false</c> when the tool did not exist or the request failed, so
    /// the caller can surface a failure without the nav silently dropping an entry it still has.
    /// </summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        try
        {
            using var response = await _http.DeleteAsync($"/api/tools/{Uri.EscapeDataString(id)}", ct);
            if (!response.IsSuccessStatusCode)
                return false;
            Changed?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Wire representation of a user-defined portal tool. Mirrors the gateway's ToolDefinition record;
/// <see cref="Id"/> is carried as a plain string because the server's ToolId value object
/// serialises to its underlying string.
/// </summary>
public sealed class ToolDto
{
    /// <summary>Stable identifier for the tool. Used to build the <c>/tools/{id}</c> nav link.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display name shown in the Tools nav section.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Target URL the tool launches / embeds.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

    /// <summary>Icon shown alongside the tool (typically a single emoji); may be empty.</summary>
    [JsonPropertyName("icon")] public string Icon { get; set; } = string.Empty;

    /// <summary>Sort order within the tool list (ascending).</summary>
    [JsonPropertyName("order")] public int Order { get; set; }

    /// <summary>
    /// Whether the tool renders inside a sandboxed frame. Defaults to <c>true</c>; when the owner
    /// explicitly opts out (<c>false</c>) the iframe is rendered without the <c>sandbox</c> attribute.
    /// </summary>
    [JsonPropertyName("sandboxEnabled")] public bool SandboxEnabled { get; set; } = true;
}
