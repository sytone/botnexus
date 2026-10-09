namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Immutable characteristics of the last GC identified by the enclosing snapshot's
/// GcCollectionIndex. Counts are observations by that collection, not a current census.
/// </summary>
public sealed record LastGcCollectionSnapshot
{
    /// <summary>Copies last-collection readings without narrowing object counts.</summary>
    public LastGcCollectionSnapshot(int generation, bool compacted, bool concurrent,
        long pinnedObjectsCount, long finalizationPendingCount)
    {
        Generation = generation;
        Compacted = compacted;
        Concurrent = concurrent;
        PinnedObjectsCount = pinnedObjectsCount;
        FinalizationPendingCount = finalizationPendingCount;
    }

    /// <summary>Generation collected; younger generations are also collected.</summary>
    public int Generation { get; }

    /// <summary>Whether this was a compacting GC; not proof of process-memory reclamation.</summary>
    public bool Compacted { get; }

    /// <summary>Whether this was a concurrent (background) GC.</summary>
    public bool Concurrent { get; }

    /// <summary>Number of pinned objects observed by this GC, not the current pinned population.</summary>
    public long PinnedObjectsCount { get; }

    /// <summary>Number of objects ready for finalization observed by this GC, not the current queue size.</summary>
    public long FinalizationPendingCount { get; }
}
