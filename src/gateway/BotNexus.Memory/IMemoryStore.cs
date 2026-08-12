using BotNexus.Memory.Models;

namespace BotNexus.Memory;

public interface IMemoryStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default);
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
