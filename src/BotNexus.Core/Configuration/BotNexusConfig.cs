namespace BotNexus.Core.Configuration;

/// <summary>Root configuration object bound from the "BotNexus" section (appsettings.json + ~/.botnexus/config.json).</summary>
public class BotNexusConfig
{
    public const string SectionName = "BotNexus";

    public string ExtensionsPath { get; set; } = "~/.botnexus/extensions";
    public ExtensionLoadingConfig Extensions { get; set; } = new();
    public AgentDefaults Agents { get; set; } = new();
    public ProvidersConfig Providers { get; set; } = new();
    public ChannelsConfig Channels { get; set; } = new();
    public GatewayConfig Gateway { get; set; } = new();
    public ToolsConfig Tools { get; set; } = new();
    public ApiConfig Api { get; set; } = new();
    public CronConfig Cron { get; set; } = new();
}
