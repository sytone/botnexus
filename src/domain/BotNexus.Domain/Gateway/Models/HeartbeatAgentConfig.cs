namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Heartbeat polling configuration for an agent.
/// When enabled, the agent is periodically prompted to check HEARTBEAT.md tasks.
/// </summary>
public sealed class HeartbeatAgentConfig
{
    /// <summary>Whether heartbeat polling is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minutes between heartbeat polls. Default: 30.</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Custom heartbeat prompt. If null, uses the default:
    /// "Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK."
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>Quiet hours configuration -- skip heartbeats during these hours.</summary>
    public QuietHoursConfig? QuietHours { get; set; }

    /// <summary>
    /// Active hours configuration -- restrict heartbeats to a time window.
    /// When set, the cron expression is generated to only fire within the specified window.
    /// Takes precedence over <see cref="QuietHours"/> for schedule generation.
    /// </summary>
    public ActiveHoursConfig? ActiveHours { get; set; }

    /// <summary>
    /// Maximum character length of an assistant response that can be classified as a
    /// heartbeat acknowledgement. Responses that contain "HEARTBEAT_OK" but are longer
    /// than this threshold are treated as substantive replies (not pruned).
    /// Default: 300.
    /// </summary>
    public int AckMaxChars { get; set; } = 300;
}

/// <summary>
/// Quiet hours configuration for heartbeat polling.
/// </summary>
public sealed class QuietHoursConfig
{
    /// <summary>Whether quiet hours are enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Start of quiet period (local time, format "HH:mm"). Default: "23:00".</summary>
    public string Start { get; set; } = "23:00";

    /// <summary>End of quiet period (local time, format "HH:mm"). Default: "07:00".</summary>
    public string End { get; set; } = "07:00";

    /// <summary>Timezone for quiet hours. Falls back to agent's soul timezone or "UTC".</summary>
    public string? Timezone { get; set; }
}

/// <summary>
/// Active hours configuration -- restrict heartbeats to a specific time window.
/// The provisioner bakes these hours directly into the cron expression so the scheduler
/// only fires within the window.
/// </summary>
/// <remarks>
/// Midnight-spanning windows (e.g. 22:00-06:00) are not supported in a single standard
/// cron expression. Configure <see cref="QuietHoursConfig"/> for inverted ranges,
/// or split into two heartbeat agents.
/// </remarks>
public sealed class ActiveHoursConfig
{
    /// <summary>Start of active window (local time, "HH:mm"). Default: "08:00".</summary>
    public string Start { get; set; } = "08:00";

    /// <summary>
    /// End of active window (local time, "HH:mm"). Default: "23:00".
    /// Must be strictly later than <see cref="Start"/>; midnight-spanning ranges are not supported.
    /// </summary>
    public string End { get; set; } = "23:00";

    /// <summary>
    /// IANA timezone for the active window. Falls back to agent soul timezone or UTC.
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>
    /// Parses "HH:mm" and returns (hour, minute). Returns null if the format is invalid.
    /// </summary>
    public static (int Hour, int Minute)? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        if (h < 0 || h > 23 || m < 0 || m > 59) return null;
        return (h, m);
    }

    /// <summary>
    /// Validates that Start and End form a non-spanning forward window.
    /// Returns an error message, or null if valid.
    /// </summary>
    public string? Validate()
    {
        var start = ParseTime(Start);
        var end = ParseTime(End);

        if (start is null) return $"ActiveHours.Start '{Start}' is not a valid HH:mm time.";
        if (end is null) return $"ActiveHours.End '{End}' is not a valid HH:mm time.";

        var startMinutes = start.Value.Hour * 60 + start.Value.Minute;
        var endMinutes = end.Value.Hour * 60 + end.Value.Minute;

        if (endMinutes <= startMinutes)
            return $"ActiveHours.End '{End}' must be strictly later than Start '{Start}'. Midnight-spanning windows are not supported.";

        return null;
    }
}
