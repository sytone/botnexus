using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Lightweight diagnostics endpoint for channel-side error reports and log pattern monitoring.
/// Authenticated by <see cref="GatewayAuthMiddleware"/> (same as all other /api/* endpoints).
/// </summary>
[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController(
    ILogger<DiagnosticsController> logger,
    LogDiagnosticsRingBuffer? logBuffer = null,
    MemoryPressureMonitor? memoryMonitor = null,
    IThreadPoolMetrics? threadPoolMetrics = null,
    IOptions<ThreadPoolWatchdogOptions>? threadPoolOptions = null,
    IActivityTracker? activityTracker = null,
    IOptions<LivenessWatchdogOptions>? livenessOptions = null) : ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger = logger;
    private readonly LogDiagnosticsRingBuffer? _logBuffer = logBuffer;
    private readonly MemoryPressureMonitor? _memoryMonitor = memoryMonitor;
    private readonly IThreadPoolMetrics? _threadPoolMetrics = threadPoolMetrics;
    private readonly ThreadPoolWatchdogOptions? _threadPoolOptions = threadPoolOptions?.Value;
    private readonly IActivityTracker? _activityTracker = activityTracker;
    private readonly LivenessWatchdogOptions? _livenessOptions = livenessOptions?.Value;

    /// <summary>
    /// Accepts an error report from any channel adapter and logs it at Error level.
    /// Authentication is enforced by <see cref="GatewayAuthMiddleware"/> for all /api/* paths.
    /// </summary>
    [HttpPost("channel-error")]
    public IActionResult ReportChannelError([FromBody] ChannelErrorReport report)
    {
        if (report is null)
            return BadRequest("Report body is required.");

        _logger.LogError(
            "Channel error reported. Agent={AgentId} Session={SessionId} Url={Url} UserAgent={UserAgent} Timestamp={Timestamp} Message={Message}\nComponentStack={ComponentStack}\nStackTrace={StackTrace}",
            Sanitise(report.AgentId),
            Sanitise(report.SessionId),
            Sanitise(report.Url),
            Sanitise(report.UserAgent),
            report.Timestamp,
            Sanitise(report.Message),
            Sanitise(report.ComponentStack),
            Sanitise(report.StackTrace));

        return Ok();
    }

    /// <summary>
    /// Returns deduplicated Warning/Error log patterns observed within a time window.
    /// </summary>
    /// <param name="hours">Time window in hours (default 24, max 168).</param>
    /// <param name="page">Zero-based page number (default 0).</param>
    /// <param name="pageSize">Results per page (default 50, max 200).</param>
    [HttpGet("log-patterns")]
    public IActionResult GetLogPatterns(
        [FromQuery] int hours = 24,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50)
    {
        if (_logBuffer is null)
            return NotFound("Log diagnostics not enabled.");

        hours = Math.Clamp(hours, 1, 168);
        page = Math.Max(page, 0);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var patterns = _logBuffer.GetPatterns(TimeSpan.FromHours(hours));
        var total = patterns.Count;
        var paged = patterns
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(p => new LogPatternDto
            {
                Fingerprint = p.Fingerprint,
                Template = p.Template,
                Severity = p.Severity.ToString(),
                Count = p.Count,
                FirstSeen = p.FirstSeen,
                LastSeen = p.LastSeen,
                SampleMessage = p.SampleMessage
            })
            .ToList();

        return Ok(new LogPatternsResponse
        {
            Patterns = paged,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Hours = hours
        });
    }

    /// <summary>
    /// Returns a point-in-time memory pressure snapshot with readable metrics,
    /// threshold ratios, and actionable operator guidance.
    /// </summary>
    [HttpGet("memory-pressure")]
    public IActionResult GetMemoryPressure()
    {
        if (_memoryMonitor is null)
            return NotFound("Memory pressure monitoring not enabled.");

        var snapshot = _memoryMonitor.CaptureSnapshot();
        return Ok(new MemoryPressureDto
        {
            CapturedAt = snapshot.CapturedAt,
            WorkingSetBytes = snapshot.WorkingSetBytes,
            GcCommittedBytes = snapshot.GcCommittedBytes,
            TotalAvailableBytes = snapshot.TotalAvailableBytes,
            Gen0Collections = snapshot.Gen0Collections,
            Gen1Collections = snapshot.Gen1Collections,
            Gen2Collections = snapshot.Gen2Collections,
            PressurePercent = snapshot.PressurePercent,
            WorkingSetReadable = snapshot.WorkingSetReadable,
            GcCommittedReadable = snapshot.GcCommittedReadable,
            TotalAvailableReadable = snapshot.TotalAvailableReadable,
            Level = snapshot.Level.ToString(),
            Guidance = snapshot.Guidance
        });
    }

    /// <summary>
    /// Returns recent memory pressure snapshots for trend analysis.
    /// </summary>
    /// <param name="count">Number of recent snapshots to return (default 10, max 100).</param>
    [HttpGet("memory-pressure/history")]
    public IActionResult GetMemoryPressureHistory([FromQuery] int count = 10)
    {
        if (_memoryMonitor is null)
            return NotFound("Memory pressure monitoring not enabled.");

        count = Math.Clamp(count, 1, 100);
        var history = _memoryMonitor.GetHistory(count);

        return Ok(new MemoryPressureHistoryResponse
        {
            Snapshots = history.Select(s => new MemoryPressureDto
            {
                CapturedAt = s.CapturedAt,
                WorkingSetBytes = s.WorkingSetBytes,
                GcCommittedBytes = s.GcCommittedBytes,
                TotalAvailableBytes = s.TotalAvailableBytes,
                Gen0Collections = s.Gen0Collections,
                Gen1Collections = s.Gen1Collections,
                Gen2Collections = s.Gen2Collections,
                PressurePercent = s.PressurePercent,
                WorkingSetReadable = s.WorkingSetReadable,
                GcCommittedReadable = s.GcCommittedReadable,
                TotalAvailableReadable = s.TotalAvailableReadable,
                Level = s.Level.ToString(),
                Guidance = s.Guidance
            }).ToList(),
            Count = history.Count,
            SnapshotsRetained = _memoryMonitor.SnapshotCount
        });
    }

    /// <summary>
    /// Strips newline and carriage-return characters from a user-supplied string to prevent
    /// log injection (CodeQL: cs/log-forging). Null-safe.
    /// </summary>
    private static string? Sanitise(string? value) =>
        value?.Replace("\r", string.Empty, StringComparison.Ordinal)
              .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>
    /// Returns a point-in-time threadpool snapshot with health assessment.
    /// </summary>
    [HttpGet("threadpool")]
    public IActionResult GetThreadpool()
    {
        if (_threadPoolMetrics is null)
            return NotFound("Threadpool diagnostics not enabled.");

        var counts = _threadPoolMetrics.GetThreadCounts();
        var pending = _threadPoolMetrics.PendingWorkItemCount;
        var threshold = _threadPoolOptions?.QueueDepthThreshold ?? 100;

        return Ok(new ThreadPoolSnapshotDto
        {
            PendingWorkItems = pending,
            WorkerAvailable = counts.WorkerAvailable,
            WorkerMax = counts.WorkerMax,
            WorkerMin = counts.WorkerMin,
            IoAvailable = counts.IoAvailable,
            IoMax = counts.IoMax,
            IoMin = counts.IoMin,
            IsHealthy = pending < threshold,
            QueueDepthThreshold = threshold
        });
    }

    /// <summary>
    /// Returns gateway activity tracking snapshot with health assessment.
    /// </summary>
    [HttpGet("activity")]
    public IActionResult GetActivity()
    {
        if (_activityTracker is null)
            return NotFound("Activity tracking not enabled.");

        var inactivity = _activityTracker.TimeSinceLastActivity;
        var threshold = _livenessOptions?.WarningThreshold ?? TimeSpan.FromMinutes(5);

        return Ok(new ActivitySnapshotDto
        {
            LastActivityUtc = _activityTracker.LastActivityUtc,
            InactivitySeconds = (long)inactivity.TotalSeconds,
            IsHealthy = inactivity < threshold,
            WarningThresholdSeconds = (long)threshold.TotalSeconds
        });
    }
}

/// <summary>
/// DTO for a single log pattern returned by the log-patterns endpoint.
/// </summary>
public sealed class LogPatternDto
{
    /// <summary>Stable fingerprint derived from the message template hash.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The original message template (e.g., "Failed for {SessionId}").</summary>
    public required string Template { get; init; }

    /// <summary>Log severity level (Warning, Error, Critical).</summary>
    public required string Severity { get; init; }

    /// <summary>Number of times this pattern has been observed in the window.</summary>
    public required int Count { get; init; }

    /// <summary>Timestamp of the first observation.</summary>
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>Timestamp of the most recent observation.</summary>
    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>A sample rendered message from the first observation (truncated to 500 chars).</summary>
    public required string SampleMessage { get; init; }
}

/// <summary>
/// Response wrapper for paginated log patterns.
/// </summary>
public sealed class LogPatternsResponse
{
    /// <summary>The page of patterns matching the query.</summary>
    public required IReadOnlyList<LogPatternDto> Patterns { get; init; }

    /// <summary>Total number of patterns matching the time window.</summary>
    public required int Total { get; init; }

    /// <summary>Zero-based page number.</summary>
    public required int Page { get; init; }

    /// <summary>Results per page.</summary>
    public required int PageSize { get; init; }

    /// <summary>Time window in hours that was queried.</summary>
    public required int Hours { get; init; }
}

/// <summary>
/// DTO for a single memory pressure snapshot.
/// </summary>
public sealed class MemoryPressureDto
{
    /// <summary>When the snapshot was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Process working set (RSS) in bytes.</summary>
    public required long WorkingSetBytes { get; init; }

    /// <summary>GC committed bytes (managed heap + overhead).</summary>
    public required long GcCommittedBytes { get; init; }

    /// <summary>Total available memory as reported by GC.</summary>
    public required long TotalAvailableBytes { get; init; }

    /// <summary>Gen0 collection count.</summary>
    public required int Gen0Collections { get; init; }

    /// <summary>Gen1 collection count.</summary>
    public required int Gen1Collections { get; init; }

    /// <summary>Gen2 collection count.</summary>
    public required int Gen2Collections { get; init; }

    /// <summary>Percentage of available memory committed by GC (0-100).</summary>
    public required double PressurePercent { get; init; }

    /// <summary>Human-readable RSS (e.g. "142.3 MB").</summary>
    public required string WorkingSetReadable { get; init; }

    /// <summary>Human-readable GC committed (e.g. "98.7 MB").</summary>
    public required string GcCommittedReadable { get; init; }

    /// <summary>Human-readable total available (e.g. "2.0 GB").</summary>
    public required string TotalAvailableReadable { get; init; }

    /// <summary>Pressure level: Normal, Elevated, Critical.</summary>
    public required string Level { get; init; }

    /// <summary>Actionable next-step guidance for the operator.</summary>
    public required string Guidance { get; init; }
}

/// <summary>
/// Response wrapper for memory pressure history.
/// </summary>
public sealed class MemoryPressureHistoryResponse
{
    /// <summary>Recent snapshots ordered newest-first.</summary>
    public required IReadOnlyList<MemoryPressureDto> Snapshots { get; init; }

    /// <summary>Number of snapshots in this response.</summary>
    public required int Count { get; init; }

    /// <summary>Total snapshots retained in the ring buffer.</summary>
    public required int SnapshotsRetained { get; init; }
}

/// <summary>
/// Point-in-time threadpool snapshot including health assessment.
/// </summary>
public sealed class ThreadPoolSnapshotDto
{
    /// <summary>Number of pending work items in the threadpool queue.</summary>
    public required long PendingWorkItems { get; init; }

    /// <summary>Available worker threads.</summary>
    public required int WorkerAvailable { get; init; }

    /// <summary>Maximum worker threads.</summary>
    public required int WorkerMax { get; init; }

    /// <summary>Minimum worker threads.</summary>
    public required int WorkerMin { get; init; }

    /// <summary>Available IO completion port threads.</summary>
    public required int IoAvailable { get; init; }

    /// <summary>Maximum IO completion port threads.</summary>
    public required int IoMax { get; init; }

    /// <summary>Minimum IO completion port threads.</summary>
    public required int IoMin { get; init; }

    /// <summary>True when pending work items are below the configured threshold.</summary>
    public required bool IsHealthy { get; init; }

    /// <summary>Configured queue depth threshold above which the pool is considered unhealthy.</summary>
    public required int QueueDepthThreshold { get; init; }
}

/// <summary>
/// Gateway activity tracking snapshot with health assessment.
/// </summary>
public sealed class ActivitySnapshotDto
{
    /// <summary>UTC timestamp of the last recorded gateway activity.</summary>
    public required DateTimeOffset LastActivityUtc { get; init; }

    /// <summary>Seconds since last activity.</summary>
    public required long InactivitySeconds { get; init; }

    /// <summary>True when inactivity is below the configured warning threshold.</summary>
    public required bool IsHealthy { get; init; }

    /// <summary>Configured warning threshold in seconds.</summary>
    public required long WarningThresholdSeconds { get; init; }
}
