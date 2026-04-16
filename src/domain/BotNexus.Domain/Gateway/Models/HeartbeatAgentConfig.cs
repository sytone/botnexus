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

    /// <summary>Quiet hours configuration — skip heartbeats during these hours.</summary>
    public QuietHoursConfig? QuietHours { get; set; }
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
