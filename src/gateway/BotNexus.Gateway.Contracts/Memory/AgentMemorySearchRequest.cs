namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Request to search memory entries with natural language and optional filters.
/// </summary>
/// <param name="AgentId">The agent whose memory to search.</param>
/// <param name="Query">Natural language search query.</param>
/// <param name="TopK">Maximum number of results to return. Defaults to 10.</param>
/// <param name="Filter">Optional filter to narrow results by source, date, or tags.</param>
public sealed record AgentMemorySearchRequest(
    string AgentId,
    string Query,
    int TopK = 10,
    AgentMemorySearchFilter? Filter = null);

/// <summary>
/// A single search result from a memory query.
/// </summary>
/// <param name="Id">Unique identifier of the memory entry.</param>
/// <param name="Content">The full content of the entry.</param>
/// <param name="SourceType">Origin type of the entry.</param>
/// <param name="SessionId">Session that produced this entry, if any.</param>
/// <param name="CreatedAt">When the entry was created.</param>
/// <param name="RelevanceScore">
/// Provider-specific relevance score. Higher is more relevant.
/// Not comparable across different providers.
/// </param>
/// <param name="Tags">Tags associated with this entry.</param>
public sealed record AgentMemorySearchResult(
    string Id,
    string Content,
    string SourceType,
    string? SessionId,
    DateTimeOffset CreatedAt,
    double RelevanceScore = 0.0,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>
    /// Where the entry's content came from - <c>agent</c>, <c>user</c>, <c>tool</c>,
    /// <c>external-untrusted</c>, or <c>unknown</c> (issue #2480).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SourceType"/>, which says what kind of write produced the entry
    /// rather than whose words it contains. Defaults to <c>unknown</c> so a provider that does not
    /// record provenance can never have its results read as first-party. Providers must surface
    /// the <i>normalized</i> value; this issue covers the metadata only, and gating on it is #2519.
    /// </remarks>
    public string Provenance { get; init; } = "unknown";

    /// <summary>
    /// The trust tier derived from <see cref="Provenance"/> - <c>trusted</c>, <c>derived</c>,
    /// <c>untrusted</c> or <c>quarantined</c> (issue #3232).
    /// </summary>
    /// <remarks>
    /// Derived on read from the provenance above rather than carried independently, so the two can
    /// never disagree on the wire. Rendered alongside provenance rather than instead of it: the
    /// provenance says where the content came from, the tier says what the retrieval pipeline did
    /// about it, and a reader auditing a surprising result needs both.
    /// </remarks>
    public string TrustTier { get; init; } = "untrusted";

    /// <summary>Conversation the content originated in, when recorded.</summary>
    public string? OriginConversationId { get; init; }

    /// <summary>Session the content originated in, when recorded.</summary>
    public string? OriginSessionId { get; init; }
}
