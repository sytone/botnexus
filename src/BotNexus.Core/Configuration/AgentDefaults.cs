namespace BotNexus.Core.Configuration;

/// <summary>Default settings applied to all agents unless overridden.</summary>
public class AgentDefaults
{
    public string Workspace { get; set; } = "~/.botnexus/workspace";
    public string Model { get; set; } = "gpt-4o";
    public int MaxTokens { get; set; } = 8192;
    public int ContextWindowTokens { get; set; } = 65536;
    public double Temperature { get; set; } = 0.1;
    public int MaxToolIterations { get; set; } = 40;
    public string Timezone { get; set; } = "UTC";
    public Dictionary<string, AgentConfig> Named { get; set; } = [];
}
