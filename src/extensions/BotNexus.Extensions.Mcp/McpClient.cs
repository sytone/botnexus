using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Client for communicating with an MCP server over a transport.
/// Handles JSON-RPC request/response correlation and the MCP initialization handshake.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    /// <summary>
    /// Maximum number of <c>tools/list</c> pages walked before the client stops following
    /// <c>nextCursor</c>. Guards against a misbehaving or hostile server paginating forever.
    /// </summary>
    internal const int MaxToolListPages = 100;

    private readonly IMcpTransport _transport;
    private readonly string _serverId;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _protocolLock = new(1, 1);
    private int _nextId;
    private McpServerCapabilities? _capabilities;
    private bool _initialized;

    /// <summary>Creates a client bound to a transport.</summary>
    /// <param name="transport">Transport used to exchange JSON-RPC messages.</param>
    /// <param name="serverId">Server identifier used for tool name prefixing and diagnostics.</param>
    /// <param name="logger">Optional logger for protocol diagnostics.</param>
    public McpClient(IMcpTransport transport, string serverId, ILogger? logger = null)
    {
        _transport = transport;
        _serverId = serverId;
        _logger = logger ?? NullLogger.Instance;
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
        await _protocolLock.WaitAsync(ct).ConfigureAwait(false);
        var connected = false;
        try
        {
            await _transport.ConnectAsync(ct).ConfigureAwait(false);
            connected = true;

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
        catch (Exception ex) when (connected)
        {
            // Issue #2723: the transport may already have spawned a child process. Tear it down
            // before the handshake exception propagates, otherwise a failing server leaks one
            // credential-holding process per attempt. Teardown must never mask the original error.
            await TearDownAfterFailedHandshakeAsync(ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _protocolLock.Release();
        }
    }

    /// <summary>
    /// Best-effort teardown of the transport after a failed initialize handshake.
    /// Any teardown failure is logged and swallowed so the original handshake exception
    /// reaches the caller unchanged.
    /// </summary>
    private async Task TearDownAfterFailedHandshakeAsync(Exception handshakeError)
    {
        try
        {
            await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception teardownError)
        {
            _logger.LogWarning(
                teardownError,
                "MCP server {ServerId}: failed to tear down transport after initialize failed ({HandshakeError}).",
                _serverId,
                handshakeError.Message);
        }
    }

    /// <summary>
    /// Lists all tools available on the MCP server, following <c>nextCursor</c> pagination
    /// until the server reports no further pages. Termination is driven solely by an absent
    /// or empty <c>nextCursor</c> — a short page is not treated as end-of-list.
    /// </summary>
    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();

        await _protocolLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tools = new List<McpToolDefinition>();
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var pages = 0;

            while (true)
            {
                var request = new JsonRpcRequest
                {
                    Id = Interlocked.Increment(ref _nextId),
                    Method = "tools/list",
                    Params = cursor is null
                        ? null
                        : JsonSerializer.SerializeToElement(
                            new McpToolsListParams { Cursor = cursor },
                            JsonContext.Default.McpToolsListParams),
                };

                await _transport.SendAsync(request, ct).ConfigureAwait(false);
                var response = await _transport.ReceiveAsync(ct).ConfigureAwait(false);

                if (response.Error is not null)
                {
                    throw new McpException(
                        $"MCP tools/list failed: {response.Error.Message}",
                        response.Error.Code);
                }

                pages++;

                if (response.Result is not JsonElement result)
                    break;

                var toolsResult = JsonSerializer.Deserialize(result.GetRawText(), JsonContext.Default.McpToolsListResult);
                if (toolsResult is null)
                    break;

                if (toolsResult.Tools is { } page)
                    tools.AddRange(page);

                var next = toolsResult.NextCursor;
                if (string.IsNullOrEmpty(next))
                    break;

                if (!seenCursors.Add(next))
                {
                    _logger.LogWarning(
                        "MCP server '{ServerId}' repeated tools/list cursor '{Cursor}'; stopping pagination after {PageCount} page(s) with {ToolCount} tool(s).",
                        _serverId, next, pages, tools.Count);
                    break;
                }

                if (pages >= MaxToolListPages)
                {
                    _logger.LogWarning(
                        "MCP server '{ServerId}' tools/list exceeded the {MaxPages}-page cap; tool list truncated at {ToolCount} tool(s) and may be incomplete.",
                        _serverId, MaxToolListPages, tools.Count);
                    break;
                }

                cursor = next;
            }

            return tools;
        }
        finally
        {
            _protocolLock.Release();
        }
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

        await _protocolLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        finally
        {
            _protocolLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _transport.DisconnectAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _protocolLock.Dispose();
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
