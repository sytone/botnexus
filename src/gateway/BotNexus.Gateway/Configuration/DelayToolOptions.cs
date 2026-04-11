namespace BotNexus.Gateway.Configuration;

public sealed class DelayToolOptions
{
    public int MaxDelaySeconds { get; set; } = 1800; // 30 minutes
    public int DefaultDelaySeconds { get; set; } = 60;
}
