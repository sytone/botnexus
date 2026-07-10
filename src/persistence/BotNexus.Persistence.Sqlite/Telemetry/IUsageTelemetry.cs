namespace BotNexus.Persistence.Sqlite.Telemetry;

/// <summary>
/// Shared durable usage-telemetry primitive (#1850). Any extension or the platform can record
/// coarse, monotonic per-entity usage counters plus provenance and a pin flag, isolated by a
/// consumer <c>namespace</c> so multiple consumers coexist in one database. Generalizes the
/// skill-usage store shipped in #1846.
/// </summary>
/// <remarks>
/// Recording is best-effort by contract from the caller's perspective — hot-path tools that call
/// this must swallow telemetry failures so a telemetry outage cannot break real work. The store
/// itself does not swallow exceptions (so tests can assert failure behaviour directly); the
/// discipline lives at the call site facade.
/// </remarks>
public interface IUsageTelemetry
{
    /// <summary>
    /// Increments the named counter for <paramref name="key"/> within <paramref name="namespace"/>
    /// and refreshes <c>last_used_at</c>, creating the row on first touch. Counter names are
    /// arbitrary and consumer-defined (e.g. <c>view</c>, <c>use</c>, <c>patch</c>).
    /// </summary>
    Task IncrementAsync(string @namespace, string key, string counterName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a row exists for a newly created entity and stamps <c>created_by</c> for later
    /// curation. Refreshes provenance on a create-after-delete without disturbing counters.
    /// </summary>
    Task RecordCreatedAsync(string @namespace, string key, string createdBy, CancellationToken cancellationToken = default);

    /// <summary>Sets (or clears) the pinned flag that protects an entity from a future curator's archive/delete pass.</summary>
    Task SetPinnedAsync(string @namespace, string key, bool pinned, CancellationToken cancellationToken = default);

    /// <summary>Returns all rows in <paramref name="namespace"/> ordered most-recently-used first.</summary>
    Task<IReadOnlyList<UsageRecord>> GetAllAsync(string @namespace, CancellationToken cancellationToken = default);

    /// <summary>Returns the row for a single entity, or <c>null</c> if it has no recorded activity.</summary>
    Task<UsageRecord?> GetAsync(string @namespace, string key, CancellationToken cancellationToken = default);
}
