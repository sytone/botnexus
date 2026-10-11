using System.Diagnostics;
using BotNexus.Persistence.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Monitors memory pressure via GC and Process APIs, periodically captures snapshots,
/// and logs actionable warnings when thresholds are exceeded.
/// </summary>
/// <remarks>
/// Snapshots are retained in a bounded ring buffer for API consumption.
/// The monitor logs at WARN level when pressure is Elevated, and ERROR when Critical.
/// </remarks>
public sealed class MemoryPressureMonitor
{
    private readonly ILogger<MemoryPressureMonitor> _logger;
    private readonly List<MemoryPressureSnapshot> _history = new();
    private readonly object _lock = new();
    private readonly int _maxSnapshots;
    private readonly Func<SqliteAllocatorSnapshot> _captureSqliteAllocator;

    /// <summary>Threshold ratio (0-1) above which pressure is considered Elevated.</summary>
    public const double ElevatedThreshold = 0.70;

    /// <summary>Threshold ratio (0-1) above which pressure is considered Critical.</summary>
    public const double CriticalThreshold = 0.90;

    /// <summary>
    /// Creates a new memory pressure monitor.
    /// </summary>
    /// <param name="logger">Logger instance for pressure events.</param>
    /// <param name="maxSnapshots">Maximum number of snapshots to retain (default 100).</param>
    public MemoryPressureMonitor(ILogger<MemoryPressureMonitor> logger, int maxSnapshots = 100)
        : this(logger, maxSnapshots, SqliteAllocatorDiagnostics.Capture)
    {
    }

    internal MemoryPressureMonitor(ILogger<MemoryPressureMonitor> logger, int maxSnapshots,
        Func<SqliteAllocatorSnapshot> captureSqliteAllocator)
    {
        _logger = logger;
        _maxSnapshots = maxSnapshots;
        _captureSqliteAllocator = captureSqliteAllocator;
    }

    /// <summary>
    /// Captures a point-in-time memory pressure snapshot and adds it to the history ring buffer.
    /// Logs at WARN or ERROR level if pressure thresholds are exceeded.
    /// Process metrics are current samples; GC metrics describe the last reported collection.
    /// They are not an atomic measurement. The diagnostic gap does not affect pressure policy.
    /// </summary>
    /// <returns>The captured snapshot.</returns>
    public MemoryPressureSnapshot CaptureSnapshot()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();

        var workingSet = process.WorkingSet64;
        var privateMemory = process.PrivateMemorySize64;
        var gcCommitted = gcInfo.TotalCommittedBytes;
        var totalAvailable = gcInfo.TotalAvailableMemoryBytes;
        var sqliteAllocator = _captureSqliteAllocator();

        var pressurePercent = totalAvailable > 0
            ? (double)gcCommitted / totalAvailable * 100.0
            : 0.0;

        var level = pressurePercent switch
        {
            >= CriticalThreshold * 100 => MemoryPressureLevel.Critical,
            >= ElevatedThreshold * 100 => MemoryPressureLevel.Elevated,
            _ => MemoryPressureLevel.Normal
        };

        var guidance = level switch
        {
            MemoryPressureLevel.Critical =>
                "Memory pressure is critical. Consider restarting the gateway, reducing concurrent sessions, or increasing available memory.",
            MemoryPressureLevel.Elevated =>
                "Memory pressure is elevated. Monitor for growth trends. Consider compacting long-running sessions or archiving idle conversations.",
            _ =>
                "Memory usage is within normal bounds. No action required."
        };

        var snapshot = new MemoryPressureSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            WorkingSetBytes = workingSet,
            PrivateMemoryBytes = privateMemory,
            GcCommittedBytes = gcCommitted,
            GcHeapSizeBytes = gcInfo.HeapSizeBytes,
            GcFragmentedBytes = gcInfo.FragmentedBytes,
            GcCollectionIndex = gcInfo.Index,
            GcGenerations = MapGenerationInfo(gcInfo.Index, gcInfo.GenerationInfo),
            SqliteConnections = SqliteConnectionFactory.GetConnectionObservation(),
            SqliteAllocatorAvailable = sqliteAllocator.IsAvailable,
            SqliteAllocatorCurrentBytes = sqliteAllocator.CurrentBytes,
            SqliteAllocatorPeakBytes = sqliteAllocator.PeakBytes,
            UnattributedPrivateBytesAboveLastGcCommitment =
                CalculateUnattributedPrivateBytesAboveLastGcCommitment(privateMemory, gcCommitted),
            TotalAvailableBytes = totalAvailable,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            PressurePercent = Math.Round(pressurePercent, 2),
            WorkingSetReadable = FormatBytes(workingSet),
            PrivateMemoryReadable = FormatBytes(privateMemory),
            GcCommittedReadable = FormatBytes(gcCommitted),
            TotalAvailableReadable = FormatBytes(totalAvailable),
            Level = level,
            Guidance = guidance
        };

        LogIfPressured(snapshot);
        AddToHistory(snapshot);

        return snapshot;
    }

    /// <summary>
    /// Returns the most recent N snapshots (newest first).
    /// </summary>
    /// <param name="count">Number of snapshots to return (default 10, max equals maxSnapshots).</param>
    public IReadOnlyList<MemoryPressureSnapshot> GetHistory(int count = 10)
    {
        lock (_lock)
        {
            count = Math.Clamp(count, 1, _history.Count);
            return _history
                .OrderByDescending(s => s.CapturedAt)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>
    /// Returns the number of snapshots currently retained.
    /// </summary>
    public int SnapshotCount
    {
        get { lock (_lock) { return _history.Count; } }
    }

    internal void AddToHistory(MemoryPressureSnapshot snapshot)
    {
        lock (_lock)
        {
            _history.Add(snapshot);
            if (_history.Count > _maxSnapshots)
                _history.RemoveAt(0);
        }
    }

    private void LogIfPressured(MemoryPressureSnapshot snapshot)
    {
        if (snapshot.Level == MemoryPressureLevel.Critical)
        {
            _logger.LogError(
                "Memory pressure CRITICAL: {PressurePercent}% committed ({GcCommitted} / {TotalAvailable}). RSS={WorkingSet}. {Guidance}",
                snapshot.PressurePercent,
                snapshot.GcCommittedReadable,
                snapshot.TotalAvailableReadable,
                snapshot.WorkingSetReadable,
                snapshot.Guidance);
        }
        else if (snapshot.Level == MemoryPressureLevel.Elevated)
        {
            _logger.LogWarning(
                "Memory pressure elevated: {PressurePercent}% committed ({GcCommitted} / {TotalAvailable}). RSS={WorkingSet}. {Guidance}",
                snapshot.PressurePercent,
                snapshot.GcCommittedReadable,
                snapshot.TotalAvailableReadable,
                snapshot.WorkingSetReadable,
                snapshot.Guidance);
        }
    }

    // .NET 10 exposes five ordinal slots. Keep storage bounded and copy only the
    // public readings from the SAME GCMemoryInfo used for the enclosing snapshot.
    internal static IReadOnlyList<GcGenerationSnapshot> MapGenerationInfo(
        long collectionIndex, ReadOnlySpan<GCGenerationInfo> generationInfo)
    {
        if (collectionIndex == 0)
            return Array.AsReadOnly(Array.Empty<GcGenerationSnapshot>());

        var generations = new GcGenerationSnapshot[Math.Min(5, generationInfo.Length)];
        for (var slot = 0; slot < generations.Length; slot++)
        {
            var info = generationInfo[slot];
            generations[slot] = new GcGenerationSnapshot(slot,
                info.SizeBeforeBytes, info.FragmentationBeforeBytes,
                info.SizeAfterBytes, info.FragmentationAfterBytes);
        }
        return Array.AsReadOnly(generations);
    }

    // Both inputs are nonnegative byte counts, sampled at different times. Keep the
    // arithmetic in Int64 and clamp the gap without attributing it to an allocator.
    internal static long CalculateUnattributedPrivateBytesAboveLastGcCommitment(
        long privateMemoryBytes, long lastGcCommittedBytes) =>
        Math.Max(0L, privateMemoryBytes - lastGcCommittedBytes);

    /// <summary>
    /// Formats a byte count into a human-readable string (e.g. "142.3 MB").
    /// </summary>
    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";

        return bytes switch
        {
            < 1024L => $"{bytes} B",
            < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
