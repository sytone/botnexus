using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>Detached aggregate state for consistent read-only diagnostic projections.</summary>
/// <param name="Fingerprint">Capture-generated aggregate identity.</param>
/// <param name="Severity">Captured log severity.</param>
/// <param name="Count">Aggregate observations since creation or retention reset; not a window-specific count.</param>
/// <param name="FirstSeen">Oldest aggregate observation since creation or retention reset.</param>
/// <param name="LastSeen">Most recent aggregate observation.</param>
/// <param name="RecentOccurrences">Bounded evidence within the requested window, newest first.</param>
public sealed record LogPatternSnapshot(string Fingerprint, LogLevel Severity, int Count,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, IReadOnlyList<LogPatternOccurrence> RecentOccurrences);
