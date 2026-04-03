namespace BotNexus.Core.Configuration;

/// <summary>Per-agent configuration.</summary>
public class AgentConfig
{
    public string Name { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? SystemPromptFile { get; set; }
    public string? Workspace { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public int? MaxTokens { get; set; }
    public double? Temperature { get; set; }
    public int? MaxToolIterations { get; set; }
    public string? Timezone { get; set; }
    public bool? EnableMemory { get; set; }
    public int MaxContextFileChars { get; set; } = 8000;
    public string? ConsolidationModel { get; set; }
    public int MemoryConsolidationIntervalHours { get; set; } = 24;
    public bool AutoLoadMemory { get; set; } = true;
    public List<McpServerConfig> McpServers { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> DisabledSkills { get; set; } = [];
    public List<string> DisallowedTools { get; set; } = [];
    [Obsolete("Per-agent CronJobs are deprecated. Define jobs centrally in BotNexusConfig.Cron.Jobs instead.")]
    public List<CronJobConfig> CronJobs { get; set; } = [];
}
