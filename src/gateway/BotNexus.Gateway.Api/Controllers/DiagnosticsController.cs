using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Lightweight diagnostics endpoint for channel-side error reports and log pattern monitoring.
/// Authenticated by <see cref="GatewayAuthMiddleware"/> (same as all other /api/* endpoints).
/// </summary>
[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController(
    ILogger<DiagnosticsController> logger,
    LogDiagnosticsRingBuffer? logBuffer = null) : ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger = logger;
    private readonly LogDiagnosticsRingBuffer? _logBuffer = logBuffer;

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
    /// Strips newline and carriage-return characters from a user-supplied string to prevent
    /// log injection (CodeQL: cs/log-forging). Null-safe.
    /// </summary>
    private static string? Sanitise(string? value) =>
        value?.Replace("\r", string.Empty, StringComparison.Ordinal)
              .Replace("\n", " ", StringComparison.Ordinal);
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
