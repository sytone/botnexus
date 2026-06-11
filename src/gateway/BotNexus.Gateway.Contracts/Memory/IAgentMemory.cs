namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Unified memory provider interface for agent memory operations.
/// Abstracts the underlying storage mechanism (SQLite, file-based, QMD, hybrid)
/// behind a consistent API that the gateway pipeline consumes.
/// </summary>
public interface IAgentMemory
{
    /// <summary>
    /// Retrieves memory context suitable for inclusion in the agent's system prompt.
    /// The provider decides what is relevant based on the request parameters.
    /// </summary>
    Task<AgentMemoryContext> GetPromptContextAsync(AgentMemoryPromptRequest request, CancellationToken ct = default);

    /// <summary>
    /// Persists a memory entry from a session interaction, compaction summary,
    /// manual save, or dreaming consolidation.
    /// </summary>
    Task SaveAsync(AgentMemorySaveRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches memory entries using a natural language query with optional filters.
    /// Returns ranked results ordered by relevance.
    /// </summary>
    Task<IReadOnlyList<AgentMemorySearchResult>> SearchAsync(AgentMemorySearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a specific memory entry by its unique identifier.
    /// Returns null if the entry does not exist or has been archived.
    /// </summary>
    Task<AgentMemorySearchResult?> GetAsync(string entryId, CancellationToken ct = default);

    /// <summary>
    /// Called when a session ends to allow the provider to flush pending writes,
    /// update indexes, or perform session-scoped bookkeeping.
    /// </summary>
    Task OnSessionCompleteAsync(AgentMemorySessionEvent sessionEvent, CancellationToken ct = default);

    /// <summary>
    /// Performs periodic memory consolidation (dreaming). The provider merges,
    /// summarises, or archives older entries to maintain long-term coherence.
    /// </summary>
    Task ConsolidateAsync(AgentMemoryConsolidateRequest request, CancellationToken ct = default);
}
