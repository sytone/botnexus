using System.Diagnostics;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Represents a point-in-time memory pressure snapshot with readable metrics,
/// threshold ratios, and actionable operator guidance.
/// </summary>
/// <remarks>
/// Process metrics are sampled at capture time; GC metrics describe the last collection
/// reported by GCMemoryInfo. These readings are not atomic and can describe different times.
/// </remarks>
public sealed class MemoryPressureSnapshot
{
    /// <summary>Timestamp when the snapshot was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Process Resident Set Size (working set) in bytes.</summary>
    public required long WorkingSetBytes { get; init; }

    /// <summary>Process private memory sampled at capture time, not atomically with the last-GC metrics.</summary>
    public long PrivateMemoryBytes { get; init; }

    /// <summary>GC total committed bytes (managed heap + GC overhead) reported for the last collection.</summary>
    public required long GcCommittedBytes { get; init; }

    /// <summary>Heap size reported for the last GC, including fragmentation; not a current live-object byte count.</summary>
    public long GcHeapSizeBytes { get; init; }

    /// <summary>Fragmentation reported for the same last GC as GcHeapSizeBytes.</summary>
    public long GcFragmentedBytes { get; init; }

    /// <summary>Index identifying the GC represented by these metrics; zero when no collection has occurred.</summary>
    public long GcCollectionIndex { get; init; }

    /// <summary>
    /// Diagnostic gap: max(0, current process private bytes - last-GC committed bytes).
    /// The readings are non-atomic and represent different times; this difference does not
    /// attribute memory to an allocator or measure live-object bytes.
    /// </summary>
    public long UnattributedPrivateBytesAboveLastGcCommitment { get; init; }

    /// <summary>Whether the SQLite raw provider was available; false means allocator counters are null, not zero.</summary>
    public bool SqliteAllocatorAvailable { get; init; }

    /// <summary>
    /// Current bytes from the current SQLite native library allocator only; null when unavailable.
    /// Does not cover all SQLite mappings/page caches, count connections, or attribute process-native memory.
    /// </summary>
    public long? SqliteAllocatorCurrentBytes { get; init; }

    /// <summary>SQLite allocator peak bytes since the library's last reset, read without resetting; null when unavailable and non-atomic with current bytes.</summary>
    public long? SqliteAllocatorPeakBytes { get; init; }

    /// <summary>GC total available memory in bytes (as reported by GCMemoryInfo).</summary>
    public required long TotalAvailableBytes { get; init; }

    /// <summary>Current Gen0 collection count since process start.</summary>
    public required int Gen0Collections { get; init; }

    /// <summary>Current Gen1 collection count since process start.</summary>
    public required int Gen1Collections { get; init; }

    /// <summary>Current Gen2 collection count since process start.</summary>
    public required int Gen2Collections { get; init; }

    /// <summary>Percentage of total available memory currently committed by the GC (0-100).</summary>
    public required double PressurePercent { get; init; }

    /// <summary>Human-readable RSS (e.g. "142.3 MB").</summary>
    public required string WorkingSetReadable { get; init; }

    /// <summary>Human-readable private memory sampled at capture time (e.g. "142.3 MB").</summary>
    public string PrivateMemoryReadable { get; init; } = "0 B";

    /// <summary>Human-readable GC committed (e.g. "98.7 MB").</summary>
    public required string GcCommittedReadable { get; init; }

    /// <summary>Human-readable total available (e.g. "2.0 GB").</summary>
    public required string TotalAvailableReadable { get; init; }

    /// <summary>
    /// Pressure level: Normal, Elevated, Critical.
    /// </summary>
    public required MemoryPressureLevel Level { get; init; }

    /// <summary>
    /// Actionable next-step guidance for the operator based on the current pressure level.
    /// </summary>
    public required string Guidance { get; init; }
}

/// <summary>
/// Discrete pressure levels for memory diagnostics.
/// </summary>
public enum MemoryPressureLevel
{
    /// <summary>Memory usage is within normal bounds (&lt;70% of available).</summary>
    Normal,

    /// <summary>Memory usage is elevated (70-90% of available). Monitor closely.</summary>
    Elevated,

    /// <summary>Memory usage is critical (&gt;90% of available). Consider restarting or reducing load.</summary>
    Critical
}
