namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Memory dreaming configuration for periodic consolidation of daily notes into MEMORY.md.
/// When enabled, a system cron job is provisioned that runs the agent with a consolidation
/// prompt to read recent daily memory files and promote important items to MEMORY.md.
/// </summary>
public sealed class MemoryDreamingConfig
{
    /// <summary>Whether memory dreaming is enabled. Default: false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Cron schedule for the dreaming job. Default: "0 3 * * *" (3am daily).
    /// Uses standard 5-field cron syntax. Evaluated in <see cref="Timezone"/> if set.
    /// </summary>
    public string Schedule { get; set; } = "0 3 * * *";

    /// <summary>
    /// IANA timezone for the schedule. Falls back to agent soul timezone or UTC.
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>
    /// Number of days of daily notes to include in the consolidation window. Default: 7.
    /// </summary>
    public int LookbackDays { get; set; } = 7;

    /// <summary>
    /// Custom consolidation prompt. If null, uses the default prompt.
    /// </summary>
    public string? Prompt { get; set; }
}
