using BotNexus.AgentCore.Tools;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Manages multiple MCP server connections and provides bridged tools for an agent session.
/// </summary>
public sealed class McpServerManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private bool _disposed;

    /// <summary>
    /// Starts all configured MCP servers, initializes them, and returns bridged tools.
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> StartServersAsync(
        McpExtensionConfig config,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tools = new List<IAgentTool>();

        foreach (var (serverId, serverConfig) in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(serverConfig.Command))
                continue; // Only stdio transport supported in Phase 1-2

            var transport = new StdioMcpTransport(
                serverConfig.Command,
                serverConfig.Args,
                serverConfig.Env,
                serverConfig.WorkingDirectory);

            var client = new McpClient(transport, serverId);

            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            initCts.CancelAfter(serverConfig.InitTimeoutMs);

            try
            {
                await client.InitializeAsync(initCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new TimeoutException(
                    $"MCP server '{serverId}' did not initialize within {serverConfig.InitTimeoutMs}ms.");
            }

            _clients.Add(client);

            var mcpTools = await client.ListToolsAsync(ct).ConfigureAwait(false);

            foreach (var mcpTool in mcpTools)
            {
                tools.Add(new McpBridgedTool(client, mcpTool, config.ToolPrefix));
            }
        }

        return tools;
    }

    /// <summary>
    /// Returns all bridged tools across all connected servers.
    /// </summary>
    public IReadOnlyList<McpClient> GetClients()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _clients;
    }

    /// <summary>
    /// Gracefully stops all MCP server connections.
    /// </summary>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _clients.Clear();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAllAsync().ConfigureAwait(false);
    }
}
