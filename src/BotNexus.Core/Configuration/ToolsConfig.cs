namespace BotNexus.Core.Configuration;

/// <summary>Web search configuration.</summary>
public class WebSearchConfig
{
    public string Provider { get; set; } = "brave";
    public string ApiKey { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 5;
}

/// <summary>Shell execution configuration.</summary>
public class ExecConfig
{
    public bool Enable { get; set; } = true;
    public int Timeout { get; set; } = 60;
}

/// <summary>Tool configuration.</summary>
public class ToolsConfig
{
    public bool RestrictToWorkspace { get; set; } = false;
    public ExecConfig Exec { get; set; } = new();
    public WebConfig Web { get; set; } = new();

    /// <summary>Named MCP server configurations, keyed by a logical server name.</summary>
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = [];
}

/// <summary>Web tool configuration.</summary>
public class WebConfig
{
    public WebSearchConfig Search { get; set; } = new();
}
