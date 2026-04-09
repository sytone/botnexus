using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Configuration for the MCP extension, specifying servers and bridging options.
/// </summary>
public sealed class McpExtensionConfig
{
    /// <summary>MCP servers to connect to, keyed by server ID.</summary>
    [JsonPropertyName("servers")]
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new();

    /// <summary>Whether to prefix tool names with server ID. Default: true.</summary>
    [JsonPropertyName("toolPrefix")]
    public bool ToolPrefix { get; set; } = true;
}

/// <summary>
/// Configuration for a single MCP server connection.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>Command to spawn the MCP server process (stdio transport).</summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>Arguments for the server command.</summary>
    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    /// <summary>Environment variables to set for the server process.</summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>Working directory for the server process.</summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    /// <summary>Timeout for initialization in milliseconds. Default: 30000.</summary>
    [JsonPropertyName("initTimeoutMs")]
    public int InitTimeoutMs { get; set; } = 30_000;

    /// <summary>Timeout for tool calls in milliseconds. Default: 60000.</summary>
    [JsonPropertyName("callTimeoutMs")]
    public int CallTimeoutMs { get; set; } = 60_000;
}
