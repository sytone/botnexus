using System.Text.Json.Serialization;
using BotNexus.Extensions.Mcp;

namespace BotNexus.Extensions.McpInvoke;

/// <summary>
/// Configuration for the MCP Invoke extension.
/// Servers listed here are accessed on-demand via the invoke_mcp tool,
/// not bridged as individual tools.
/// </summary>
public sealed class McpInvokeConfig
{
    /// <summary>Whether the invoke_mcp tool is enabled.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>MCP servers accessible via invoke_mcp, keyed by server ID.</summary>
    [JsonPropertyName("servers")]
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new();
}
