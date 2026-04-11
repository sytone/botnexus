namespace BotNexus.Gateway.Configuration;

public sealed class SessionWarmupOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxSessionsPerAgent { get; set; } = 10;
    public int RetentionWindowHours { get; set; } = 24;

    public TimeSpan RetentionWindow
    {
        get => TimeSpan.FromHours(RetentionWindowHours);
        set => RetentionWindowHours = Math.Max(0, (int)Math.Ceiling(value.TotalHours));
    }
}
