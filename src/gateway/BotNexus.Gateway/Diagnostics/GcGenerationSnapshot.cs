namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Immutable size and fragmentation readings for one ordinal runtime generation slot,
/// on entry to and exit from the collection identified by the enclosing snapshot.
/// These are last-GC readings, not current live-object counts.
/// </summary>
public readonly struct GcGenerationSnapshot
{
    /// <summary>Copies one runtime slot's last-GC readings without narrowing byte counts.</summary>
    public GcGenerationSnapshot(int slot, long sizeBeforeBytes, long fragmentationBeforeBytes,
        long sizeAfterBytes, long fragmentationAfterBytes)
    {
        Slot = slot;
        SizeBeforeBytes = sizeBeforeBytes;
        FragmentationBeforeBytes = fragmentationBeforeBytes;
        SizeAfterBytes = sizeAfterBytes;
        FragmentationAfterBytes = fragmentationAfterBytes;
    }

    /// <summary>Zero-based ordinal in the runtime's GenerationInfo span, not a generation label.</summary>
    public int Slot { get; }

    /// <summary>Size in bytes on entry to the reported collection.</summary>
    public long SizeBeforeBytes { get; }

    /// <summary>Fragmentation in bytes on entry to the reported collection.</summary>
    public long FragmentationBeforeBytes { get; }

    /// <summary>Size in bytes on exit from the reported collection.</summary>
    public long SizeAfterBytes { get; }

    /// <summary>Fragmentation in bytes on exit from the reported collection.</summary>
    public long FragmentationAfterBytes { get; }
}
