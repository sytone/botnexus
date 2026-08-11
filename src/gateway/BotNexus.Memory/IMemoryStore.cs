using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;

namespace BotNexus.Memory;

public interface IMemoryStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    /// Searches like <see cref="SearchAsync"/> but also returns the fused relevance score that
    /// decided each row's position, so callers can render a magnitude and apply a relevance floor.
    /// </summary>
    /// <remarks>
    /// Default-implemented rather than required so the many lightweight stores and test doubles that
    /// only ever needed ordering keep compiling. The default reports <c>0</c> for every row: an
    /// honest "this store publishes no score", never a fabricated one. Stores backed by
    /// <see cref="HybridMemoryRanker"/> override it with the real fused magnitude (#2781).
    /// </remarks>
    async Task<IReadOnlyList<ScoredMemoryEntry>> SearchScoredAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
    {
        var entries = await SearchAsync(query, topK, filter, ct).ConfigureAwait(false);
        return entries.Select(entry => new ScoredMemoryEntry(entry, 0d)).ToList();
    }

    Task DeleteAsync(string id, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default);
}
