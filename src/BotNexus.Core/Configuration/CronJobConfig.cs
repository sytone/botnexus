namespace BotNexus.Core.Configuration;

/// <summary>Per-job configuration within the centralized Cron section.</summary>
public class CronJobConfig
{
    /// <summary>Cron expression (standard 5-field or 6-field with seconds).</summary>
    public string Schedule { get; set; } = string.Empty;

    /// <summary>Job type: "agent", "system", or "maintenance".</summary>
    public string Type { get; set; } = "agent";

    public bool Enabled { get; set; } = true;
    public string? Timezone { get; set; }

    // Agent job properties
    public string? Agent { get; set; }
    public string? Prompt { get; set; }
    public string? Session { get; set; }

    // System/Maintenance job properties
    public string? Action { get; set; }
    public List<string> Agents { get; set; } = [];

    // Output routing
    public List<string> OutputChannels { get; set; } = [];
}
