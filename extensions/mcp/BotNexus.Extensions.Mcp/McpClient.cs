using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Client for communicating with an MCP server over a transport.
/// Handles JSON-RPC request/response correlation and the MCP initialization handshake.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly IMcpTransport _transport;
    private readonly string _serverId;
    private int _nextId;
    private McpServerCapabilities? _capabilities;
    private bool _initialized;

    public McpClient(IMcpTransport transport, string serverId)
    {
        _transport = transport;
        _serverId = serverId;
    }

    /// <summary>Gets the server identifier used for tool name prefixing.</summary>
    public string ServerId => _serverId;

    /// <summary>Gets the server capabilities obtained during initialization.</summary>
    public McpServerCapabilities? Capabilities => _capabilities;

    /// <summary>
    /// Connects to the server and performs the MCP initialization handshake.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _transport.ConnectAsync(ct).ConfigureAwait(false);

        var initParams = new McpInitializeParams();

        var request = new JsonRpcRequest
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = "initialize",
            Params = JsonSerializer.SerializeToElement(initParams, JsonContext.Default.McpInitializeParams),
        };

        await _transport.SendAsync(request, ct).ConfigureAwait(false);
        var response = await _transport.ReceiveAsync(ct).ConfigureAwait(false);

        if (response.Error is not null)
        {
            throw new McpException(
                $"MCP initialize failed: {response.Error.Message}",
                response.Error.Code);
        }

        if (response.Result is JsonElement result)
        {
            var initResult = JsonSerializer.Deserialize(result.GetRawText(), JsonContext.Default.McpInitializeResult);
            _capabilities = initResult?.Capabilities;
        }

        // Send initialized notification per MCP spec
        var notification = new JsonRpcNotification { Method = "notifications/initialized" };
        await _transport.SendNotificationAsync(notification, ct).ConfigureAwait(false);

        _initialized = true;
    }

    /// <summary>
    /// Lists all tools available on the MCP server.
    /// </summary>
    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();

        var request = new JsonRpcRequest
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = "tools/list",
        };

        await _transport.SendAsync(request, ct).ConfigureAwait(false);
        var response = await _transport.ReceiveAsync(ct).ConfigureAwait(false);

        if (response.Error is not null)
        {
            throw new McpException(
                $"MCP tools/list failed: {response.Error.Message}",
                response.Error.Code);
        }

        if (response.Result is JsonElement result)
        {
            var toolsResult = JsonSerializer.Deserialize(result.GetRawText(), JsonContext.Default.McpToolsListResult);
            return toolsResult?.Tools ?? [];
        }

        return [];
    }

    /// <summary>
    /// Calls a tool on the MCP server.
    /// </summary>
    public async Task<McpToolCallResult> CallToolAsync(
        string toolName,
        JsonElement? arguments = null,
        CancellationToken ct = default)
    {
        EnsureInitialized();

        var callParams = new McpToolCallParams
        {
            Name = toolName,
            Arguments = arguments,
        };

        var request = new JsonRpcRequest
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(callParams, JsonContext.Default.McpToolCallParams),
        };

        await _transport.SendAsync(request, ct).ConfigureAwait(false);
        var response = await _transport.ReceiveAsync(ct).ConfigureAwait(false);

        if (response.Error is not null)
        {
            throw new McpException(
                $"MCP tools/call '{toolName}' failed: {response.Error.Message}",
                response.Error.Code);
        }

        if (response.Result is JsonElement result)
        {
            return JsonSerializer.Deserialize(result.GetRawText(), JsonContext.Default.McpToolCallResult)
                   ?? new McpToolCallResult();
        }

        return new McpToolCallResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _transport.DisconnectAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("McpClient has not been initialized. Call InitializeAsync first.");
    }
}

/// <summary>
/// Represents an error returned by an MCP server.
/// </summary>
public sealed class McpException : Exception
{
    public McpException(string message, int code) : base(message)
    {
        Code = code;
    }

    /// <summary>JSON-RPC error code.</summary>
    public int Code { get; }
}
