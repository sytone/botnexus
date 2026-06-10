namespace BotNexus.Cron;

/// <summary>
/// Configuration for automatic purging of old completed cron run records.
/// Prevents unbounded growth of the cron_runs table in SQLite.
/// </summary>
public sealed class CronRunRetentionOptions
{
    /// <summary>
    /// Number of days to retain completed/failed run records. Default: 30.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// How often the retention service checks for expired runs. Default: 1 hour.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
}
