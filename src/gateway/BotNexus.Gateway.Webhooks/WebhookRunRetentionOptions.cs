namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Configuration options for webhook run retention (automatic purge of old completed runs).
/// </summary>
public sealed class WebhookRunRetentionOptions
{
    /// <summary>
    /// Number of days to retain completed/failed/timed-out webhook runs.
    /// Runs older than this are purged. Default: 30 days.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// How often the retention sweep runs. Default: 1 hour.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
}
