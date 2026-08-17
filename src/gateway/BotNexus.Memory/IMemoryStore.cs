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

    /// <summary>
    /// Searches like <see cref="SearchScoredAsync"/> but also returns a <see cref="MemoryVectorScanReport"/>
    /// describing what the vector leg of retrieval actually examined (issue #3244).
    /// </summary>
    /// <remarks>
    /// The vector scan is bounded by <see cref="MemoryVectorSearchOptions.MaxScanRows"/> and ordered
    /// newest-first. Without this member, crossing that bound is invisible: a caller cannot tell
    /// "nothing older matched" from "nothing older was considered", so recall silently truncates on
    /// exactly the long-lived stores where old-memory recall matters most.
    /// <para>
    /// Required, not default-implemented, for exactly the reason recorded on
    /// <see cref="SearchScoredAsync"/>: Moq intercepts default interface methods and returns
    /// <see langword="null"/> rather than running the default body, so every mocked store would
    /// hand back a null task at runtime. Requiring the member makes each implementer state its
    /// scan-coverage behaviour explicitly, and the compiler enforces it. A store that runs no
    /// bounded scan has an honest one-liner available: return
    /// <see cref="MemoryVectorScanReport.NotAttempted"/>.
    /// </para>
    /// </remarks>
    Task<MemorySearchResult> SearchWithReportAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Deletes every memory row scoped to <paramref name="sessionId"/> and returns the number of
    /// rows removed (issue #2956).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Memory indexing is an additive projection of session lifecycle events, so without a
    /// session-scoped delete the memory store and the session store diverge permanently on the
    /// first session deletion: the content stays searchable and keeps surfacing in
    /// <c>memory_search</c> attributed to a session that no longer exists. Deleting a session for
    /// privacy or correctness reasons must actually remove its content from recall.
    /// </para>
    /// <para>
    /// Rows whose <c>session_id</c> is <see langword="null"/> are <b>never</b> matched. Those are
    /// non-session memories (<c>memory_save</c>, learning extractions, shared-store promotions)
    /// whose lifetime is not bound to any session. A blank or whitespace <paramref name="sessionId"/>
    /// is a no-op returning zero rather than a broad delete.
    /// </para>
    /// <para>
    /// The default implementation is a no-op returning zero so that non-SQLite stores and test
    /// doubles keep working; stores that persist <c>session_id</c> must override it.
    /// </para>
    /// </remarks>
    /// <param name="sessionId">The session whose memory rows to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>
    /// Returns the distinct non-null <c>session_id</c> values present in the store (issue #2956).
    /// </summary>
    /// <remarks>
    /// This is the scan side of startup reconciliation: the returned ids are diffed against the
    /// surviving session corpus and the ones with no matching session are pruned. Rows with a
    /// <see langword="null"/> session id are excluded by construction, so an unscoped memory can
    /// never be mistaken for an orphan. The default implementation returns an empty set so stores
    /// that do not track sessions simply have nothing to reconcile.
    /// </remarks>
    Task<IReadOnlyList<string>> ListSessionIdsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    Task ClearAsync(CancellationToken ct = default);
    Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default);
}
