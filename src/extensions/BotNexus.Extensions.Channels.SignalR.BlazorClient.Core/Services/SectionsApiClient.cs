using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Server-backed sections API. Failures are explicit: an unavailable list is never an empty
/// snapshot, and an ambiguous write must be refreshed before a caller attempts another write.
/// Legacy bool/null signatures remain source compatible, but failures throw SectionsApiException.
/// </summary>
public sealed class SectionsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _http;

    /// <summary>Uses the portal's configured transport; never stores order in browser preferences.</summary>
    public SectionsApiClient(HttpClient http) => _http = http;

    private static string Base(string agentId) => $"/api/agents/{Uri.EscapeDataString(agentId)}/sections";

    /// <summary>Reads independent constituent snapshots; consumers must reconcile their identities.</summary>
    public Task<SectionListDto> ListAsync(string agentId, CancellationToken ct = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, Base(agentId)), false, async response =>
        {
            var dto = await response.Content.ReadFromJsonAsync<SectionListDto>(JsonOptions, ct);
            if (dto is null || dto.Sections is null || dto.Assignments is null)
                throw new JsonException("Missing section snapshot.");
            return dto;
        }, ct);

    /// <summary>Creates once; a confirmed write with an unreadable body still requires refresh, not repost.</summary>
    public async Task<SectionDto?> CreateAsync(string agentId, string name, CancellationToken ct = default) =>
        await SendAsync(JsonRequest(HttpMethod.Post, Base(agentId), new { name }), true, async response =>
            await response.Content.ReadFromJsonAsync<SectionDto>(JsonOptions, ct)
                ?? throw new JsonException("Missing created section."), ct);

    /// <summary>Persists custom name/collapse changes; HTTP and transport failures are explicit.</summary>
    public Task<bool> UpdateAsync(string agentId, string sectionId, string? name, bool? isCollapsed, CancellationToken ct = default) =>
        SendAsync(JsonRequest(HttpMethod.Patch, $"{Base(agentId)}/{Uri.EscapeDataString(sectionId)}", new { name, isCollapsed }), true, Success, ct);

    /// <summary>Legacy custom-only reorder; the server retains built-in slots.</summary>
    public Task<bool> ReorderAsync(string agentId, IReadOnlyList<string> sectionIds, CancellationToken ct = default) =>
        SendAsync(JsonRequest(HttpMethod.Put, $"{Base(agentId)}/order", new { sectionIds }), true, Success, ct);

    /// <summary>Persists full peer order and returns server canonical order; unsupported servers fail explicitly.</summary>
    public Task<IReadOnlyList<SectionGroupOrderDto>> ReorderGroupsAsync(string agentId, IReadOnlyList<SectionGroupOrderDto> groupOrder, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<SectionGroupOrderDto>>(JsonRequest(HttpMethod.Put, $"{Base(agentId)}/group-order", new { groupOrder }), true,
            async response => await response.Content.ReadFromJsonAsync<List<SectionGroupOrderDto>>(JsonOptions, ct)
                ?? throw new JsonException("Missing canonical order."), ct, groupOrderWrite: true);

    /// <summary>Deletes only the custom section; conversations keep their origin and pin facts.</summary>
    public Task<bool> DeleteAsync(string agentId, string sectionId, CancellationToken ct = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Base(agentId)}/{Uri.EscapeDataString(sectionId)}"), true, Success, ct);

    /// <summary>Sets a custom override; server ownership and conversation write guards remain authoritative.</summary>
    public Task<bool> AssignAsync(string agentId, string sectionId, string conversationId, CancellationToken ct = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{Base(agentId)}/{Uri.EscapeDataString(sectionId)}/conversations/{Uri.EscapeDataString(conversationId)}"), true, Success, ct);

    /// <summary>Removes the override, allowing the next projection to restore the natural home.</summary>
    public Task<bool> UnassignAsync(string agentId, string conversationId, CancellationToken ct = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Base(agentId)}/conversations/{Uri.EscapeDataString(conversationId)}"), true, Success, ct);

    private static Task<bool> Success(HttpResponseMessage _) => Task.FromResult(true);

    private static HttpRequestMessage JsonRequest<T>(HttpMethod method, string uri, T body) =>
        new(method, uri) { Content = JsonContent.Create(body, options: JsonOptions) };

    private async Task<T> SendAsync<T>(HttpRequestMessage request, bool write, Func<HttpResponseMessage, Task<T>> read,
        CancellationToken ct, bool groupOrderWrite = false)
    {
        using (request)
        {
            var confirmed = false;
            try
            {
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var unsupported = groupOrderWrite && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;
                    // A server error may follow persistence. Do not promise that retrying POST is safe.
                    throw new SectionsApiException(unsupported ? "Server support for group ordering is required. Refresh before retrying."
                        : "The sections request failed.", response.StatusCode, write && (int)response.StatusCode >= 500, false, unsupported);
                }
                confirmed = write;
                return await read(response);
            }
            catch (SectionsApiException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
            {
                throw new SectionsApiException(confirmed ? "Saved, but the response could not be read. Refresh before continuing."
                    : write ? "The save outcome is unknown. Refresh before continuing." : "Sections could not be loaded. Retry the refresh.",
                    null, write && !confirmed, confirmed, false, ex);
            }
        }
    }
}

/// <summary>Structured failure information; never use exception text or timestamps as a version token.</summary>
public sealed class SectionsApiException : Exception
{
    /// <summary>Describes safe recovery without exposing server response bodies.</summary>
    public SectionsApiException(string message, HttpStatusCode? statusCode = null, bool outcomeUnknown = false,
        bool writeConfirmed = false, bool unsupportedGroupOrder = false, Exception? inner = null) : base(message, inner)
    {
        StatusCode = statusCode;
        OutcomeUnknown = outcomeUnknown;
        WriteConfirmed = writeConfirmed;
        UnsupportedGroupOrder = unsupportedGroupOrder;
    }
    /// <summary>HTTP status when a response was received.</summary>
    public HttpStatusCode? StatusCode { get; }
    /// <summary>The request may have saved; refresh before retrying any mutation.</summary>
    public bool OutcomeUnknown { get; }
    /// <summary>A success status was received even though its representation was unreadable.</summary>
    public bool WriteConfirmed { get; }
    /// <summary>The unified order endpoint is unavailable; no browser-only fallback is permitted.</summary>
    public bool UnsupportedGroupOrder { get; }
}

/// <summary>Independent section, assignment and optional order snapshots from one GET.</summary>
public sealed class SectionListDto
{
    /// <summary>Explicit empty data for fixtures/callers; never used for failed reads.</summary>
    public static readonly SectionListDto Empty = new();
    /// <summary>Custom records only, retaining the legacy ordering field.</summary>
    [JsonPropertyName("sections"), JsonRequired]
    public IReadOnlyList<SectionDto> Sections { get; set; } = [];
    /// <summary>Assignment overrides; stale targets are possible during concurrent mutations.</summary>
    [JsonPropertyName("assignments"), JsonRequired]
    public IReadOnlyDictionary<string, string> Assignments { get; set; } = new Dictionary<string, string>();
    /// <summary>Absent/null on older successful servers; use a read-only default projection.</summary>
    [JsonPropertyName("groupOrder")]
    public IReadOnlyList<SectionGroupOrderDto>? GroupOrder { get; set; }
}

/// <summary>Wire union; reconciliation rejects both/neither and unknown/deleted custom identities.</summary>
public sealed record SectionGroupOrderDto(
    [property: JsonPropertyName("system"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BuiltInConversationSection? System = null,
    [property: JsonPropertyName("sectionId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SectionId = null);

/// <summary>Wire representation of a custom section; names never identify system groups.</summary>
public sealed class SectionDto
{
    /// <summary>Stable custom identity, not a display label.</summary>
    [JsonPropertyName("sectionId")] public string SectionId { get; set; } = string.Empty;
    /// <summary>Ownership is checked again by the client when reconciling mixed snapshots.</summary>
    [JsonPropertyName("agentId")] public string AgentId { get; set; } = string.Empty;
    /// <summary>User-authored heading.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    /// <summary>Legacy position among custom groups only.</summary>
    [JsonPropertyName("order")] public int Order { get; set; }
    /// <summary>Server-backed custom collapse preference.</summary>
    [JsonPropertyName("isCollapsed")] public bool IsCollapsed { get; set; }
}
