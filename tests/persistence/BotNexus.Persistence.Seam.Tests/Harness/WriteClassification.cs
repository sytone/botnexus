namespace BotNexus.Persistence.Seam.Tests.Harness;

/// <summary>
/// How a single persistence entry point writes an aggregate. Classifying every mutation path is
/// the first acceptance clause of issue #2130: a lost update is only possible where a
/// <see cref="FullReplace"/> write can interleave with an independent narrower write, so the
/// classification is what tells you which pairs are worth a seam test at all.
/// </summary>
public enum WriteClassification
{
    /// <summary>Inserts a new aggregate and fails if it already exists.</summary>
    Create,

    /// <summary>
    /// Rewrites every caller-owned column (and, for conversations, the whole binding set) from a
    /// detached snapshot. The dangerous shape: any field the snapshot has gone stale on is
    /// reverted unless the write is guarded.
    /// </summary>
    FullReplace,

    /// <summary>Writes only the named columns it owns and leaves every other column as committed.</summary>
    NarrowPatch,

    /// <summary>
    /// Merges supplied items into a collection without removing anything, so concurrent producers
    /// cannot clobber each other. Idempotent by construction.
    /// </summary>
    Merge,

    /// <summary>
    /// Compare-and-swap: the write is conditional on the revision the caller's snapshot was read
    /// at, and is rejected outright when the committed row has moved on.
    /// </summary>
    CompareAndSwap,

    /// <summary>
    /// A write guarded by an external fence token (e.g. an owning session or lease) rather than by
    /// the aggregate's own revision.
    /// </summary>
    Fenced,
}

/// <summary>
/// One row of the aggregate write inventory: an entry point, how it writes, what state it owns,
/// and whether it can lose an update.
/// </summary>
/// <param name="Aggregate">The aggregate the entry point mutates, e.g. <c>conversations</c>.</param>
/// <param name="EntryPoint">The store method name.</param>
/// <param name="Classification">How the write is shaped.</param>
/// <param name="OwnedState">The state this entry point is allowed to write.</param>
/// <param name="Guard">
/// What prevents this write from silently losing a concurrent update, or the reason none is
/// needed.
/// </param>
public sealed record AggregateWriteEntry(
    string Aggregate,
    string EntryPoint,
    WriteClassification Classification,
    string OwnedState,
    string Guard);
