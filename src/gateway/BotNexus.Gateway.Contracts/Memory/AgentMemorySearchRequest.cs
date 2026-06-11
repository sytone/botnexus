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
    IReadOnlyList<string>? Tags = null);
