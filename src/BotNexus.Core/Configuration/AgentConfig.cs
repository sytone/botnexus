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
    public List<McpServerConfig> McpServers { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<CronJobConfig> CronJobs { get; set; } = [];
}
