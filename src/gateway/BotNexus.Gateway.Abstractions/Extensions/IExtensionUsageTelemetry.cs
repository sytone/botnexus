using BotNexus.Persistence.Sqlite.Telemetry;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// A namespaced durable usage-telemetry handle for a single extension (#1852). Wraps the shared
/// <see cref="IUsageTelemetry"/> primitive (#1850) and pins every operation to the consumer
/// namespace equal to the extension id, so an extension records coarse, monotonic per-entity usage
/// counters into the shared store <b>without newing up its own SQLite file</b> and without being
/// able to read or clobber another consumer's rows. This is exactly how the Skills extension's
/// <c>SqliteSkillUsageStore</c> uses namespace <c>"skills"</c>, but obtained through injected
/// abstractions rather than a bespoke store.
/// </summary>
/// <remarks>
/// Recording is best-effort by contract from the caller's perspective: hot-path tools must swallow
/// telemetry failures so a telemetry outage cannot break real work (the underlying store does not
/// swallow, so tests can assert failure behaviour). The counter names are consumer-defined.
/// </remarks>
public interface IExtensionUsageTelemetry
{
    /// <summary>The extension id used as the consumer namespace isolating these rows.</summary>
    string ExtensionId { get; }

    /// <summary>Increments the named counter for <paramref name="key"/> and refreshes freshness, creating the row on first touch.</summary>
    Task IncrementAsync(string key, string counterName, CancellationToken cancellationToken = default);

    /// <summary>Ensures a row exists for a newly created entity and stamps <paramref name="createdBy"/> provenance.</summary>
    Task RecordCreatedAsync(string key, string createdBy, CancellationToken cancellationToken = default);

    /// <summary>Sets (or clears) the pinned flag protecting an entity from a future curator's archive pass.</summary>
    Task SetPinnedAsync(string key, bool pinned, CancellationToken cancellationToken = default);

    /// <summary>Returns all rows for this extension ordered most-recently-used first.</summary>
    Task<IReadOnlyList<UsageRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the row for a single entity, or <c>null</c> if it has no recorded activity.</summary>
    Task<UsageRecord?> GetAsync(string key, CancellationToken cancellationToken = default);
}
