namespace BotNexus.Core.Configuration;

/// <summary>A scheduled cron job configuration.</summary>
public class CronJobConfig
{
    public string Name { get; set; } = string.Empty;
    public string Schedule { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Timezone { get; set; }
    public string? TargetChannel { get; set; }
    public string? TargetChatId { get; set; }
}
