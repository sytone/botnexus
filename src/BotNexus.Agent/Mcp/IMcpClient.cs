using System.Text.Json.Nodes;

namespace BotNexus.Agent.Mcp;

/// <summary>Abstraction over an MCP server connection, enabling testability of <see cref="McpTool"/>.</summary>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>Discovered remote tool schemas, keyed by tool name.</summary>
    IReadOnlyDictionary<string, JsonObject> RemoteTools { get; }

    /// <summary>Performs the MCP initialize handshake and populates <see cref="RemoteTools"/>.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Calls a remote tool by name with JSON arguments.</summary>
    Task<string> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken = default);
}
