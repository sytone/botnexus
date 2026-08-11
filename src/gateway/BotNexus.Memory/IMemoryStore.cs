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
    /// Deliberately a REQUIRED member rather than a default-implemented one. A default implementation
    /// compiles more stubs unchanged, but Moq intercepts default interface methods and returns
    /// <see langword="null"/> instead of running the default body - so every mocked store would have
    /// silently produced a null task at runtime rather than a degraded-but-correct result. Requiring
    /// the member makes each implementer state its scoring behaviour explicitly, and the compiler
    /// enforces it (#2781).
    /// </remarks>
    Task<IReadOnlyList<ScoredMemoryEntry>> SearchScoredAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default);
}
