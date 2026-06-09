using System.Diagnostics;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Represents a point-in-time memory pressure snapshot with readable metrics,
/// threshold ratios, and actionable operator guidance.
/// </summary>
public sealed class MemoryPressureSnapshot
{
    /// <summary>Timestamp when the snapshot was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Process Resident Set Size (working set) in bytes.</summary>
    public required long WorkingSetBytes { get; init; }

    /// <summary>GC total committed bytes (managed heap + GC overhead).</summary>
    public required long GcCommittedBytes { get; init; }

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
