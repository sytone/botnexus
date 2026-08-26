using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;
namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Heartbeat polling configuration for an agent.
/// When enabled, the agent is periodically prompted to check HEARTBEAT.md tasks.
/// </summary>
public sealed class HeartbeatAgentConfig
{
    /// <summary>Whether heartbeat polling is enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether heartbeat polling is enabled.",
        GroupName = "Heartbeat",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "heartbeat", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes between heartbeat polls. Default: 30.</summary>
    [Display(
        Name = "Interval minutes",
        Description = "Minutes between heartbeat polls. Default: 30.",
        GroupName = "Heartbeat",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "heartbeat", Order = 1)]
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Custom heartbeat prompt. If null, uses the default:
    /// "Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK."
    /// </summary>
    [Display(
        Name = "Prompt",
        Description = "Custom heartbeat prompt. If null, uses the default: \"Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK.\".",
        GroupName = "Heartbeat",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "heartbeat", Order = 2)]
    public string? Prompt { get; set; }

    /// <summary>Quiet hours configuration -- skip heartbeats during these hours.</summary>
    [Display(
        Name = "Quiet hours",
        Description = "Quiet hours configuration -- skip heartbeats during these hours.",
        GroupName = "Heartbeat",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "heartbeat", Order = 3)]
    public QuietHoursConfig? QuietHours { get; set; }

    /// <summary>
    /// Active hours configuration -- restrict heartbeats to a time window.
    /// When set, the cron expression is generated to only fire within the specified window.
    /// Takes precedence over <see cref="QuietHours"/> for schedule generation.
    /// </summary>
    [Display(
        Name = "Active hours",
        Description = "Active hours configuration -- restrict heartbeats to a time window. When set, the cron expression is generated to only fire within the specified window. Takes precedence over QuietHours for schedule generation.",
        GroupName = "Heartbeat",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "heartbeat", Order = 4)]
    public ActiveHoursConfig? ActiveHours { get; set; }

    /// <summary>
    /// Maximum character length of an assistant response that can be classified as a
    /// heartbeat acknowledgement. Responses that contain "HEARTBEAT_OK" but are longer
    /// than this threshold are treated as substantive replies (not pruned).
    /// Default: 300.
    /// </summary>
    [Display(
        Name = "Ack max chars",
        Description = "Maximum character length of an assistant response that can be classified as a heartbeat acknowledgement. Responses that contain \"HEARTBEAT_OK\" but are longer than this threshold are treated as substantive replies (not pruned). Default: 300.",
        GroupName = "Heartbeat",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "heartbeat", Order = 5)]
    public int AckMaxChars { get; set; } = 300;
}

/// <summary>
/// Quiet hours configuration for heartbeat polling.
/// </summary>
public sealed class QuietHoursConfig
{
    /// <summary>Whether quiet hours are enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether quiet hours are enabled.",
        GroupName = "Quiet hours",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "quiet-hours", Order = 0)]
    public bool Enabled { get; set; }

    /// <summary>Start of quiet period (local time, format "HH:mm"). Default: "23:00".</summary>
    [Display(
        Name = "Start",
        Description = "Start of quiet period (local time, format \"HH:mm\"). Default: \"23:00\".",
        GroupName = "Quiet hours",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "quiet-hours", Order = 1)]
    public string Start { get; set; } = "23:00";

    /// <summary>End of quiet period (local time, format "HH:mm"). Default: "07:00".</summary>
    [Display(
        Name = "End",
        Description = "End of quiet period (local time, format \"HH:mm\"). Default: \"07:00\".",
        GroupName = "Quiet hours",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "quiet-hours", Order = 2)]
    public string End { get; set; } = "07:00";

    /// <summary>Timezone for quiet hours. Falls back to agent's soul timezone or "UTC".</summary>
    [Display(
        Name = "Timezone",
        Description = "Timezone for quiet hours. Falls back to agent's soul timezone or \"UTC\".",
        GroupName = "Quiet hours",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "quiet-hours", Order = 3)]
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
    [Display(
        Name = "Start",
        Description = "Start of active window (local time, \"HH:mm\"). Default: \"08:00\".",
        GroupName = "Active hours",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "active-hours", Order = 0)]
    public string Start { get; set; } = "08:00";

    /// <summary>
    /// End of active window (local time, "HH:mm"). Default: "23:00".
    /// Must be strictly later than <see cref="Start"/>; midnight-spanning ranges are not supported.
    /// </summary>
    [Display(
        Name = "End",
        Description = "End of active window (local time, \"HH:mm\"). Default: \"23:00\". Must be strictly later than Start; midnight-spanning ranges are not supported.",
        GroupName = "Active hours",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "active-hours", Order = 1)]
    public string End { get; set; } = "23:00";

    /// <summary>
    /// IANA timezone for the active window. Falls back to agent soul timezone or UTC.
    /// </summary>
    [Display(
        Name = "Timezone",
        Description = "IANA timezone for the active window. Falls back to agent soul timezone or UTC.",
        GroupName = "Active hours",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "active-hours", Order = 2)]
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
