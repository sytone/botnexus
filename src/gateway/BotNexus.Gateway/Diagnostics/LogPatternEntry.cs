using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Represents a deduplicated log pattern aggregated by message template fingerprint.
/// </summary>
public sealed class LogPatternEntry
{
    /// <summary>
    /// Stable fingerprint derived from the message template (hash).
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// The original message template (e.g., "Auto-compaction failed for session {SessionId}").
    /// </summary>
    public required string Template { get; init; }

    /// <summary>
    /// Log severity level.
    /// </summary>
    public required LogLevel Severity { get; init; }

    /// <summary>
    /// Number of times this pattern has been observed.
    /// </summary>
    public int Count { get; set; } = 1;

    /// <summary>
    /// Timestamp of the first observation.
    /// </summary>
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>
    /// Timestamp of the most recent observation.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// A sample rendered message from the first observation.
    /// </summary>
    public required string SampleMessage { get; init; }
}
