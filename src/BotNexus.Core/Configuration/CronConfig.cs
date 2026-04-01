namespace BotNexus.Core.Configuration;

/// <summary>Top-level cron service configuration section.</summary>
public class CronConfig
{
    public bool Enabled { get; set; } = true;
    public int TickIntervalSeconds { get; set; } = 10;
    public int ExecutionHistorySize { get; set; } = 100;
    public Dictionary<string, CronJobConfig> Jobs { get; set; } = [];
}
