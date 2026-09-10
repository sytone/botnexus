using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>One agent's memory store, as the overview page lists it.</summary>
public sealed record MemoryStoreRowDto
{
    [JsonPropertyName("agentId")]
    public string AgentId { get; init; } = string.Empty;

    [JsonPropertyName("entryCount")]
    public int EntryCount { get; init; }

    [JsonPropertyName("databaseSizeBytes")]
    public long DatabaseSizeBytes { get; init; }

    [JsonPropertyName("lastIndexedAt")]
    public DateTimeOffset? LastIndexedAt { get; init; }

    [JsonPropertyName("embeddedEntryCount")]
    public int EmbeddedEntryCount { get; init; }

    [JsonPropertyName("vectorScanCeiling")]
    public int? VectorScanCeiling { get; init; }

    /// <summary>
    /// Projected server-side (#3244): the store holds more embedded rows than a single vector
    /// search will scan, so older entries are reachable lexically but never semantically.
    /// </summary>
    [JsonPropertyName("exceedsVectorScanCeiling")]
    public bool ExceedsVectorScanCeiling { get; init; }
}

/// <summary>One search hit: a truncated preview plus the provenance a delete decision turns on.</summary>
public sealed record MemorySearchHitDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>Truncated at 200 characters server-side. Never load this into an editor.</summary>
    [JsonPropertyName("contentPreview")]
    public string ContentPreview { get; init; } = string.Empty;

    [JsonPropertyName("provenance")]
    public string Provenance { get; init; } = "unknown";

    [JsonPropertyName("trustTier")]
    public string TrustTier { get; init; } = "Untrusted";

    [JsonPropertyName("isFirstParty")]
    public bool IsFirstParty { get; init; }

    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>How completely the store was scanned for a search (#3244).</summary>
public sealed record MemoryVectorScanDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "NotAttempted";

    [JsonPropertyName("possiblyTruncated")]
    public bool PossiblyTruncated { get; init; }

    [JsonPropertyName("explanation")]
    public string? Explanation { get; init; }
}

/// <summary>A search response: ranked hits plus the scan report that qualifies them.</summary>
public sealed record MemorySearchResponseDto
{
    [JsonPropertyName("entries")]
    public IReadOnlyList<MemorySearchHitDto> Entries { get; init; } = [];

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("vectorScan")]
    public MemoryVectorScanDto? VectorScan { get; init; }
}

/// <summary>
/// Typed access to the <c>/api/memory</c> endpoints the memory overview page uses.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately covers only the three routes that page needs — list stores, search, delete. Adding
/// and editing notes already has a home in the agent persona drawer, which talks to
/// <c>POST</c>/<c>PUT</c>/<c>entries/recent</c> directly; duplicating those here would give the
/// portal two implementations of the same write path and no obvious owner.
/// </para>
/// <para>
/// The drawer's inline calls are deliberately left alone rather than refactored onto this client.
/// Sharing the plumbing would be tidier, but rewriting a surface that landed hours ago, in the area
/// of the tree currently seeing the most traffic, is a larger and riskier change than the page
/// itself — and the two surfaces overlap on exactly one verb. Worth doing later, as its own change.
/// </para>
/// </remarks>
/// <summary>
/// A memory store shared between agents, with the access lists that define who reaches it.
/// </summary>
public sealed record SharedMemoryStoreDto
{
    /// <summary>Store name, unique across the gateway.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What the store is for, as the operator described it.</summary>
    public string? Description { get; init; }

    /// <summary>Reader access list verbatim, which may be a single "*".</summary>
    public IReadOnlyList<string> Readers { get; init; } = [];

    /// <summary>Writer access list verbatim, which may be a single "*".</summary>
    public IReadOnlyList<string> Writers { get; init; } = [];

    /// <summary>Days entries are kept, or null for indefinitely.</summary>
    public int? RetentionDays { get; init; }

    /// <summary>Agents on the current roster that can read it, with "*" resolved.</summary>
    public int ReaderCount { get; init; }

    /// <summary>Agents on the current roster that can write to it, with "*" resolved.</summary>
    public int WriterCount { get; init; }
}

public sealed class MemoryApiClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Every agent with memory enabled, with its store statistics.</summary>
    public async Task<IReadOnlyList<MemoryStoreRowDto>> GetStoresAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<MemoryStoreRowDto>>("api/memory", ct).ConfigureAwait(false) ?? [];

    /// <summary>
    /// The shared stores and their access lists. Empty on a gateway with none configured, which
    /// is the default and not an error.
    /// </summary>
    public async Task<IReadOnlyList<SharedMemoryStoreDto>> GetSharedStoresAsync(CancellationToken ct = default)
    {
        // A gateway that predates the endpoint answers 404. That is "no shared stores", not a
        // failure the page should report - GetFromJsonAsync would throw and blank the whole page.
        using var response = await _http.GetAsync("api/memory/shared", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return [];

        return await response.Content
            .ReadFromJsonAsync<List<SharedMemoryStoreDto>>(cancellationToken: ct)
            .ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// Searches one agent's entries. Returns <see langword="null"/> when the agent is unknown or has
    /// memory disabled, which the page renders as an empty state rather than as an error.
    /// </summary>
    public async Task<MemorySearchResponseDto?> SearchAsync(
        string agentId, string query, int limit = 50, CancellationToken ct = default)
    {
        var url = $"api/memory/{Uri.EscapeDataString(agentId)}/entries" +
                  $"?query={Uri.EscapeDataString(query)}&limit={limit}";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content
            .ReadFromJsonAsync<MemorySearchResponseDto>(cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes one entry. True when the row is gone — which includes it having been absent already,
    /// because the route is idempotent by design and the page must not report that as a failure.
    /// </summary>
    public async Task<bool> DeleteEntryAsync(string agentId, string entryId, CancellationToken ct = default)
    {
        var url = $"api/memory/{Uri.EscapeDataString(agentId)}/entries/{Uri.EscapeDataString(entryId)}";
        using var response = await _http.DeleteAsync(url, ct).ConfigureAwait(false);
        return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK;
    }
}
