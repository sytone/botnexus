namespace BotNexus.Core.Configuration;

/// <summary>Default settings applied to all agents unless overridden.</summary>
public class AgentDefaults
{
    public string Workspace { get; set; } = "~/.botnexus/workspace";
    public string? Model { get; set; }
    public int? MaxTokens { get; set; }
    public int? ContextWindowTokens { get; set; }
    public double? Temperature { get; set; }
    public int MaxToolIterations { get; set; } = 40;
    public int? MaxRepeatedToolCalls { get; set; }
    public string Timezone { get; set; } = "UTC";
    public Dictionary<string, AgentConfig> Named { get; set; } = [];
}
