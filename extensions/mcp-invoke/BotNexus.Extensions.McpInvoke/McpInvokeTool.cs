using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.Mcp;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.McpInvoke;

/// <summary>
/// Single-entry-point tool for calling MCP servers on demand.
/// Servers start lazily on first use and stay alive for the session.
/// Skills describe which servers and tools to call — the agent never sees
/// individual MCP tool descriptions, keeping the tool list clean.
/// </summary>
public sealed class McpInvokeTool : IAgentTool, IAsyncDisposable
{
    private readonly McpInvokeConfig _config;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _disposed;

    public McpInvokeTool(McpInvokeConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Internal test constructor that allows injecting pre-built clients.
    /// </summary>
    internal McpInvokeTool(
        McpInvokeConfig config,
        ConcurrentDictionary<string, McpClient> clientsDict,
        ILogger? logger = null)
    {
        _config = config;
        _clients = clientsDict;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public string Name => "invoke_mcp";

    /// <inheritdoc />
    public string Label => "MCP Invoke";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Call a tool on an MCP server. Skills know which server and tool to use for each domain.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["call", "list_tools", "list_servers"],
                  "description": "Action: 'call' executes a tool, 'list_tools' shows tools on a server, 'list_servers' shows configured servers."
                },
                "server": {
                  "type": "string",
                  "description": "Server name from config (required for 'call' and 'list_tools')."
                },
                "tool": {
                  "type": "string",
                  "description": "MCP tool name to call (required for 'call')."
                },
                "arguments": {
                  "type": "object",
                  "description": "Tool arguments (for 'call')."
                }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = ReadString(arguments, "action") ?? "call";

        return action.ToLowerInvariant() switch
        {
            "list_servers" => ListServers(),
            "list_tools" => await ListToolsAsync(arguments, cancellationToken).ConfigureAwait(false),
            "call" => await CallToolAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => TextResult($"Unknown action: {action}. Use 'call', 'list_tools', or 'list_servers'.")
        };
    }

    /// <inheritdoc />
    public string? GetPromptSnippet()
        => "invoke_mcp: Call MCP server tools on demand. Load a skill first to learn which server/tool to use.";

    /// <inheritdoc />
    public IReadOnlyList<string> GetPromptGuidelines() =>
    [
        "Before calling invoke_mcp, load the relevant skill to understand which MCP server and tool to use.",
        "Use invoke_mcp with action='list_servers' to see available MCP servers.",
        "Use invoke_mcp with action='list_tools' and server='{name}' to discover tools on a server.",
    ];

    #region Actions

    private AgentToolResult ListServers()
    {
        if (_config.Servers.Count == 0)
            return TextResult("No MCP servers configured.");

        var lines = new List<string> { "## Available MCP Servers", "" };
        foreach (var (id, serverConfig) in _config.Servers)
        {
            var transport = !string.IsNullOrWhiteSpace(serverConfig.Url) ? "HTTP/SSE" : "stdio";
            var status = _clients.ContainsKey(id) ? "🟢 running" : "⚪ not started";
            lines.Add($"- **{id}** ({transport}) — {status}");
        }

        return TextResult(string.Join("\n", lines));
    }

    private async Task<AgentToolResult> ListToolsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var serverId = ReadString(arguments, "server");
        if (string.IsNullOrWhiteSpace(serverId))
            return TextResult("Error: 'server' is required for list_tools.");

        var client = await GetOrStartClientAsync(serverId, ct).ConfigureAwait(false);
        if (client is null)
            return TextResult($"Error: Server '{serverId}' is not configured or failed to start.");

        try
        {
            var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);

            if (tools.Count == 0)
                return TextResult($"Server '{serverId}' has no tools.");

            var lines = new List<string> { $"## Tools on '{serverId}' ({tools.Count})", "" };
            foreach (var tool in tools)
            {
                var desc = tool.Description is not null && tool.Description.Length > 120
                    ? tool.Description[..120] + "…"
                    : tool.Description ?? "";
                lines.Add($"- **{tool.Name}**: {desc}");
            }

            return TextResult(string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list tools on MCP server '{ServerId}'.", serverId);
            return TextResult($"Error listing tools on '{serverId}': {ex.Message}");
        }
    }

    private async Task<AgentToolResult> CallToolAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var serverId = ReadString(arguments, "server");
        if (string.IsNullOrWhiteSpace(serverId))
            return TextResult("Error: 'server' is required for call.");

        var toolName = ReadString(arguments, "tool");
        if (string.IsNullOrWhiteSpace(toolName))
            return TextResult("Error: 'tool' is required for call.");

        var client = await GetOrStartClientAsync(serverId, ct).ConfigureAwait(false);
        if (client is null)
            return TextResult($"Error: Server '{serverId}' is not configured or failed to start.");

        // Resolve call timeout from server config
        var callTimeoutMs = _config.Servers.TryGetValue(serverId, out var serverConfig)
            ? serverConfig.CallTimeoutMs
            : 60_000;

        var toolArgs = ReadArguments(arguments);

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(callTimeoutMs);

        try
        {
            var result = await client.CallToolAsync(toolName, toolArgs, callCts.Token).ConfigureAwait(false);

            var contentBlocks = new List<AgentToolContent>();
            foreach (var content in result.Content)
            {
                if (content.Type is "text" && content.Text is not null)
                    contentBlocks.Add(new AgentToolContent(AgentToolContentType.Text, content.Text));
                else if (content.Type is "image" && content.Text is not null)
                    contentBlocks.Add(new AgentToolContent(AgentToolContentType.Image, content.Text));
            }

            if (contentBlocks.Count == 0)
                contentBlocks.Add(new AgentToolContent(AgentToolContentType.Text, "[no content]"));

            return new AgentToolResult(contentBlocks);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TextResult($"Error: Tool call '{toolName}' on '{serverId}' timed out after {callTimeoutMs}ms.");
        }
        catch (McpException ex)
        {
            return TextResult($"MCP error ({ex.Code}): {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to call tool '{ToolName}' on MCP server '{ServerId}'.", toolName, serverId);

            // Connection might be dead — evict so next call reconnects
            EvictClient(serverId);

            return TextResult($"Error calling '{toolName}' on '{serverId}': {ex.Message}");
        }
    }

    #endregion

    #region Server Lifecycle

    /// <summary>
    /// Gets an existing client or starts the server and initializes a new one.
    /// Thread-safe — concurrent callers for the same server will wait on the lock.
    /// </summary>
    internal async Task<McpClient?> GetOrStartClientAsync(string serverId, CancellationToken ct)
    {
        if (_clients.TryGetValue(serverId, out var existing))
            return existing;

        if (!_config.Servers.TryGetValue(serverId, out var serverConfig))
        {
            _logger.LogWarning("MCP server '{ServerId}' is not configured.", serverId);
            return null;
        }

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_clients.TryGetValue(serverId, out existing))
                return existing;

            var transport = McpServerManager.CreateTransport(serverConfig);
            if (transport is null)
            {
                _logger.LogWarning("MCP server '{ServerId}' has no command or URL configured.", serverId);
                return null;
            }

            var client = new McpClient(transport, serverId);

            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            initCts.CancelAfter(serverConfig.InitTimeoutMs);

            try
            {
                _logger.LogInformation("Starting MCP server '{ServerId}'...", serverId);
                await client.InitializeAsync(initCts.Token).ConfigureAwait(false);
                _logger.LogInformation("MCP server '{ServerId}' initialized.", serverId);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                _logger.LogWarning(
                    "MCP server '{ServerId}' did not initialize within {TimeoutMs}ms.",
                    serverId, serverConfig.InitTimeoutMs);
                return null;
            }
            catch (Exception ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                _logger.LogWarning(ex, "MCP server '{ServerId}' failed to initialize.", serverId);
                return null;
            }

            _clients[serverId] = client;
            return client;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void EvictClient(string serverId)
    {
        if (_clients.TryRemove(serverId, out var client))
        {
            _ = Task.Run(async () =>
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort cleanup */ }
            });
        }
    }

    #endregion

    #region Argument Helpers

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString()
        };
    }

    private static JsonElement? ReadArguments(IReadOnlyDictionary<string, object?> args)
    {
        if (!args.TryGetValue("arguments", out var value) || value is null) return null;
        return value switch
        {
            JsonElement el => el,
            _ => JsonSerializer.SerializeToElement(value)
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    #endregion

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients.Values)
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
        _startLock.Dispose();
    }
}
