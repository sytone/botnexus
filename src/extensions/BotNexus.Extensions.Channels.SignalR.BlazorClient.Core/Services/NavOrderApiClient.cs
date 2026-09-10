using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Client for the gateway portal nav-order REST API (<c>/api/nav-order</c>, #2236, slice 5 of
/// #2231). The nav-order model gives every built-in left-nav item a default order number and lets
/// the user override any item's order; overrides persist server-side so the ordering roams with the
/// user across browsers and devices. This client is the portal's source of truth for sorting the
/// whole left nav.
/// </summary>
public sealed class NavOrderApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>Initialises the client over the portal's configured <see cref="HttpClient"/>.</summary>
    public NavOrderApiClient(HttpClient http) => _http = http;

    /// <summary>
    /// Lists every built-in nav item with its effective order (defaults layered with user
    /// overrides), ascending. Returns an empty list (never null) on any failure so the layout can
    /// fall back to its own built-in default ordering rather than throwing.
    /// </summary>
    public async Task<IReadOnlyList<NavItemOrderDto>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<NavItemOrderDto>>("/api/nav-order", JsonOptions, ct);
            return result ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Lists the left-nav entries contributed by loaded extensions, ordered. Returns an empty list
    /// (never null) on any failure: a sidebar that renders only its built-ins is correct, whereas
    /// one that throws because an extension is unreachable is not.
    /// </summary>
    public async Task<IReadOnlyList<NavContributionDto>> ListContributionsAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<NavContributionDto>>(
                "/api/nav/contributions", JsonOptions, ct);

            // Re-check renderability here even though the gateway already validates. Deserialising
            // an unexpected document - an endpoint that does not exist yet, a proxy error page, a
            // stub answering every path alike - yields entries with empty ids and paths, and
            // rendering those puts blank rows in the sidebar. Dropping them costs nothing and
            // makes the nav impossible to corrupt from the wire.
            return result?
                .Where(c => !string.IsNullOrWhiteSpace(c.Id)
                    && !string.IsNullOrWhiteSpace(c.Label)
                    && !string.IsNullOrWhiteSpace(c.Path)
                    && c.Path.StartsWith('/')
                    && !c.Path.StartsWith("//", StringComparison.Ordinal))
                .ToList()
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Overrides the order for a single nav key and returns the full updated ordered list. Returns
    /// an empty list on failure so the caller can keep its current ordering.
    /// </summary>
    public async Task<IReadOnlyList<NavItemOrderDto>> SetOrderAsync(string key, int order, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return [];

        try
        {
            using var response = await _http.PutAsJsonAsync(
                $"/api/nav-order/{Uri.EscapeDataString(key)}",
                new NavOrderUpdateDto { Order = order },
                JsonOptions,
                ct);
            if (!response.IsSuccessStatusCode)
                return [];
            var result = await response.Content.ReadFromJsonAsync<List<NavItemOrderDto>>(JsonOptions, ct);
            return result ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Wire representation of a nav item's effective sidebar order.</summary>
public sealed class NavItemOrderDto
{
    /// <summary>Stable nav key (e.g. <c>tools</c>, <c>chat</c>).</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    /// <summary>Effective order number; lower renders higher in the sidebar.</summary>
    [JsonPropertyName("order")] public int Order { get; set; }
}

/// <summary>One left-nav entry contributed by a loaded extension.</summary>
public sealed class NavContributionDto
{
    /// <summary>Stable nav key, unique across contributions.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>Text shown in the sidebar.</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

    /// <summary>Site-relative path the entry navigates to.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    /// <summary>Portal icon name, or null to let the portal choose.</summary>
    [JsonPropertyName("icon")] public string? Icon { get; set; }

    /// <summary>Effective order number, on the same scale as the built-in nav items.</summary>
    [JsonPropertyName("order")] public int Order { get; set; }

    /// <summary>Whether the path is served outside the Blazor router.</summary>
    [JsonPropertyName("external")] public bool External { get; set; }

    /// <summary>Whether the view replaces the window rather than being hosted inside the portal.</summary>
    [JsonPropertyName("fullPage")] public bool FullPage { get; set; }

    /// <summary>Extension that contributed the entry.</summary>
    [JsonPropertyName("extensionId")] public string ExtensionId { get; set; } = string.Empty;
}

/// <summary>Request body for overriding a nav item's order.</summary>
public sealed class NavOrderUpdateDto
{
    /// <summary>The new order number; lower renders higher in the sidebar.</summary>
    [JsonPropertyName("order")] public int Order { get; set; }
}
