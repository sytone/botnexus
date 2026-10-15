using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Admin-only, read-only safe previews of structured Warning+ diagnostics (partial #4010).</summary>
[ApiController]
[Route("api/diagnostics/events")]
public sealed class StructuredDiagnosticsController(LogDiagnosticsRingBuffer? buffer = null) : ControllerBase
{
    /// <summary>Lists aggregates newest first; filters use only validated, retained occurrence evidence.</summary>
    [HttpGet]
    public IActionResult GetEvents([FromQuery] int hours = 24, [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50, [FromQuery] string? severity = null,
        [FromQuery] string? category = null, [FromQuery] string? agentId = null)
    {
        if (!IsAdminCaller()) return StatusCode(StatusCodes.Status403Forbidden);
        if (severity is not null && ParseSeverity(severity) is null)
            return BadRequest(new { error = "Severity must be Warning, Error, or Critical." });
        if (category is not null && StructuredDiagnosticPreview.Identifier(category) is null ||
            agentId is not null && StructuredDiagnosticPreview.Identifier(agentId) is null)
            return BadRequest(new { error = "Invalid diagnostic filter." });
        if (buffer is null) return NotFound();

        hours = Math.Clamp(hours, 1, 168);
        page = Math.Max(page, 0);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var selectedSeverity = severity is null ? null : ParseSeverity(severity);
        var snapshots = buffer.GetSnapshots(TimeSpan.FromHours(hours))
            .Where(snapshot => ParseSeverity(snapshot.Severity.ToString()) is not null &&
                (selectedSeverity is null || snapshot.Severity == selectedSeverity))
            .Select(snapshot => (Snapshot: snapshot, Occurrences: snapshot.RecentOccurrences
                .Select(StructuredDiagnosticPreview.Occurrence)
                .Where(occurrence => (category is null || occurrence.Category == category) &&
                    (agentId is null || occurrence.Properties["AgentId"] as string == agentId)).ToArray()))
            .Where(item => item.Occurrences.Length > 0).ToArray();
        // Multiplication is deliberately widened before evaluating a hostile page number.
        var offset = (long)page * pageSize;
        var events = offset >= snapshots.Length ? [] : snapshots.Skip((int)offset).Take(pageSize)
            .Select(item => Summary(item.Snapshot, item.Occurrences)).ToArray();
        return Ok(new StructuredDiagnosticsResponse(events, snapshots.Length, hours, page, pageSize));
    }

    /// <summary>Returns a safe detail preview with at most twenty occurrences; never exports raw log text.</summary>
    [HttpGet("{fingerprint}")]
    public IActionResult GetEvent(string fingerprint, [FromQuery] int hours = 24)
    {
        if (!IsAdminCaller()) return StatusCode(StatusCodes.Status403Forbidden);
        if (StructuredDiagnosticPreview.Hex(fingerprint, 16) is null)
            return BadRequest(new { error = "Fingerprint must contain exactly 16 hexadecimal characters." });
        if (buffer is null) return NotFound();
        var snapshot = buffer.GetSnapshots(TimeSpan.FromHours(Math.Clamp(hours, 1, 168)))
            .FirstOrDefault(item => string.Equals(item.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null || ParseSeverity(snapshot.Severity.ToString()) is null) return NotFound();
        var occurrences = snapshot.RecentOccurrences.Take(20).Select(StructuredDiagnosticPreview.Occurrence).ToArray();
        if (occurrences.Length == 0) return NotFound();
        return Ok(new StructuredDiagnosticDetail(Summary(snapshot, occurrences), occurrences));
    }

    private bool IsAdminCaller() => HttpContext?.Items.TryGetValue(GatewayAuthHttpContext.CallerIdentityItemKey, out var value) == true &&
        value is GatewayCallerIdentity { IsAdmin: true };

    private static LogLevel? ParseSeverity(string value) => value.ToLowerInvariant() switch
    {
        "warning" => LogLevel.Warning, "error" => LogLevel.Error, "critical" => LogLevel.Critical, _ => null
    };

    private static StructuredDiagnosticSummary Summary(LogPatternSnapshot snapshot, IReadOnlyList<StructuredDiagnosticOccurrence> occurrences) =>
        new(snapshot.Fingerprint, snapshot.Severity.ToString(), occurrences[0].Category,
            snapshot.Count, snapshot.FirstSeen, snapshot.LastSeen);
}

/// <summary>Bounded list response; Total counts matching aggregates before pagination.</summary>
/// <param name="Events">Safe aggregate previews.</param>
/// <param name="Total">Matching aggregate count, not occurrence count.</param>
/// <param name="Hours">Effective window, clamped to 1–168 hours.</param>
/// <param name="Page">Effective zero-based nonnegative page.</param>
/// <param name="PageSize">Effective page size, clamped to 1–200.</param>
public sealed record StructuredDiagnosticsResponse(IReadOnlyList<StructuredDiagnosticSummary> Events, int Total, int Hours, int Page, int PageSize);

/// <summary>Safe aggregate metadata; Count and timestamps are aggregate-wide, not filter/window-specific.</summary>
/// <param name="Fingerprint">Opaque capture-generated identity; not an approval identity.</param>
/// <param name="Severity">Warning, Error, or Critical.</param>
/// <param name="Category">Validated category of the newest matching retained occurrence; null if invalid.</param>
/// <param name="Count">Aggregate count since creation or retention reset.</param>
/// <param name="FirstSeen">Aggregate start since creation or retention reset.</param>
/// <param name="LastSeen">Most recent aggregate observation.</param>
public record StructuredDiagnosticSummary(string Fingerprint, string Severity, string? Category, int Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

/// <summary>Safe aggregate detail, with no message, template, source text, or arbitrary properties.</summary>
public sealed record StructuredDiagnosticDetail : StructuredDiagnosticSummary
{
    /// <summary>Detached newest-first evidence, capped at twenty observations.</summary>
    public IReadOnlyList<StructuredDiagnosticOccurrence> RecentOccurrences { get; }

    /// <summary>Creates a detail response from an already-safe summary and occurrence projection.</summary>
    public StructuredDiagnosticDetail(StructuredDiagnosticSummary summary, IReadOnlyList<StructuredDiagnosticOccurrence> occurrences)
        : base(summary.Fingerprint, summary.Severity, summary.Category, summary.Count, summary.FirstSeen, summary.LastSeen) => RecentOccurrences = occurrences;
}

/// <summary>Safe, validated occurrence metadata. Missing or invalid evidence remains null.</summary>
/// <param name="Timestamp">UTC capture time.</param>
/// <param name="Category">Validated logger category.</param>
/// <param name="EventId">Numeric logger event identity.</param>
/// <param name="EventName">Validated symbolic event name.</param>
/// <param name="Properties">Fixed-schema validated evidence; never the ring's arbitrary property map.</param>
public sealed record StructuredDiagnosticOccurrence(DateTimeOffset Timestamp, string? Category, int EventId, string? EventName, IReadOnlyDictionary<string, object?> Properties);
