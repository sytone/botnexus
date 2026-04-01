namespace BotNexus.Core.Configuration;

/// <summary>Configuration for a single channel (Telegram, Discord, Slack).</summary>
public class ChannelConfig
{
    public bool Enabled { get; set; } = false;
    public string BotToken { get; set; } = string.Empty;
    public List<string> AllowFrom { get; set; } = [];
}

/// <summary>Configuration for all channels.</summary>
public class ChannelsConfig
{
    public bool SendProgress { get; set; } = true;
    public bool SendToolHints { get; set; } = false;
    public int SendMaxRetries { get; set; } = 3;
    public Dictionary<string, ChannelConfig> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
