namespace BotNexus.Persistence.Sqlite.Telemetry;

/// <summary>
/// A single namespaced usage-telemetry row (#1850). Generalizes the skill-usage record shipped in
/// #1846: a <see cref="Namespace"/>-scoped <see cref="Key"/>, a set of named monotonic
/// <see cref="Counters"/> (arbitrary counter names such as <c>view</c>/<c>use</c>/<c>patch</c>),
/// a <see cref="LastUsedAt"/> freshness signal, provenance (<see cref="CreatedBy"/>), and a
/// <see cref="Pinned"/> flag that a future curator uses to protect an entity from an archive pass.
/// </summary>
/// <remarks>
/// The counters are intentionally coarse and monotonic: they answer "which entities earn their
/// keep" rather than reconstructing an audit trail. Counter names not yet touched read back as
/// <c>0</c> via <see cref="GetCounter"/> rather than being absent, so consumers never null-check.
/// </remarks>
public sealed record UsageRecord
{
    /// <summary>The consumer namespace that isolates this row from other consumers (e.g. <c>"skills"</c>).</summary>
    public required string Namespace { get; init; }

    /// <summary>The entity key within the namespace (e.g. a skill name). Unique per namespace.</summary>
    public required string Key { get; init; }

    /// <summary>The named monotonic counters recorded for this entity. Missing names imply zero.</summary>
    public required IReadOnlyDictionary<string, long> Counters { get; init; }

    /// <summary>When the entity was last touched (any counter incremented); <c>null</c> if never.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Identity that created the entity through its owning tool, or <c>null</c> if unknown.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>When true, the entity is protected from a future curator's archive/delete pass.</summary>
    public bool Pinned { get; init; }

    /// <summary>Returns the value of the named counter, or <c>0</c> when the counter has never been incremented.</summary>
    public long GetCounter(string counterName)
        => Counters.TryGetValue(counterName, out var value) ? value : 0;
}
