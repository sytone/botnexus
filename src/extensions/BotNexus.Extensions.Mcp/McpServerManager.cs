using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Mcp.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Manages multiple MCP server connections and provides bridged tools for an agent session.
/// </summary>
public sealed class McpServerManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpServerManager"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostics. If null, uses NullLogger.</param>
    public McpServerManager(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Starts all configured MCP servers, initializes them, and returns bridged tools.
    /// Failures on individual servers are logged as warnings and skipped; successful servers continue.
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> StartServersAsync(
        McpExtensionConfig config,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tools = new List<IAgentTool>();

        foreach (var (serverId, serverConfig) in config.Servers)
        {
            // Wrap entire per-server initialization in try/catch to isolate failures
            try
            {
                if (!TryCreateTransport(serverConfig, out var transport, out var transportError))
                {
                    _logger.LogWarning(
                        "MCP server '{ServerId}' has an insecure or invalid url: {ErrorMessage} Skipping server.",
                        serverId,
                        transportError);
                    continue;
                }

                if (transport is null)
                    continue;

                var client = new McpClient(transport, serverId, _logger);

                using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                initCts.CancelAfter(serverConfig.InitTimeoutMs);

                try
                {
                    await client.InitializeAsync(initCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Per-server timeout — log warning and skip this server
                    await client.DisposeAsync().ConfigureAwait(false);
                    _logger.LogWarning(
                        "MCP server '{ServerId}' did not initialize within {TimeoutMs}ms. Skipping server.",
                        serverId,
                        serverConfig.InitTimeoutMs);
                    continue;
                }
                catch (Exception ex)
                {
                    // InitializeAsync may throw McpException, process crashes, JSON errors, etc.
                    await client.DisposeAsync().ConfigureAwait(false);
                    _logger.LogWarning(
                        ex,
                        "MCP server '{ServerId}' failed to initialize: {ErrorMessage}. Skipping server.",
                        serverId,
                        ex.Message);
                    continue;
                }

                _clients.Add(client);

                var mcpTools = await client.ListToolsAsync(ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "MCP server '{ServerId}' initialized successfully with {ToolCount} tool(s).",
                    serverId,
                    mcpTools.Count);

                foreach (var mcpTool in mcpTools)
                {
                    tools.Add(new McpBridgedTool(client, mcpTool, config.ToolPrefix));
                }
            }
            catch (Exception ex)
            {
                // Catch any unexpected transport creation or other failures
                _logger.LogWarning(
                    ex,
                    "Failed to start MCP server '{ServerId}': {ErrorMessage}. Skipping server.",
                    serverId,
                    ex.Message);
                continue;
            }
        }

        return tools;
    }

    /// <summary>
    /// Starts a single MCP server, initializes it, and returns bridged tools.
    /// Thread-safe — can be called concurrently for multiple servers.
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> StartSingleServerAsync(
        string serverId,
        McpServerConfig serverConfig,
        bool useToolPrefix = true,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryCreateTransport(serverConfig, out var transport, out var transportError))
        {
            _logger.LogWarning(
                "MCP server '{ServerId}' has an insecure or invalid url: {ErrorMessage} Skipping server.",
                serverId,
                transportError);
            return [];
        }

        if (transport is null)
            return [];

        var client = new McpClient(transport, serverId, _logger);

        using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initCts.CancelAfter(serverConfig.InitTimeoutMs);

        try
        {
            await client.InitializeAsync(initCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning(
                "MCP server '{ServerId}' did not initialize within {TimeoutMs}ms. Skipping server.",
                serverId, serverConfig.InitTimeoutMs);
            return [];
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning(ex,
                "MCP server '{ServerId}' failed to initialize: {ErrorMessage}. Skipping server.",
                serverId, ex.Message);
            return [];
        }

        lock (_clients)
        {
            _clients.Add(client);
        }

        var mcpTools = await client.ListToolsAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "MCP server '{ServerId}' initialized successfully with {ToolCount} tool(s).",
            serverId, mcpTools.Count);

        return mcpTools.Select(t => (IAgentTool)new McpBridgedTool(client, t, useToolPrefix)).ToList();
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

    /// <summary>
    /// Creates the appropriate transport for a server configuration.
    /// Returns null if neither command nor URL is configured, or if the URL fails
    /// <see cref="McpUrlSecurity"/> validation.
    /// </summary>
    public static IMcpTransport? CreateTransport(McpServerConfig serverConfig)
        => TryCreateTransport(serverConfig, out var transport, out _) ? transport : null;

    /// <summary>
    /// Creates the appropriate transport for a server configuration, reporting why the URL was
    /// rejected when validation fails so the caller can log a warning naming the server id.
    /// </summary>
    /// <param name="serverConfig">The server configuration.</param>
    /// <param name="transport">The created transport, or <c>null</c>.</param>
    /// <param name="error">The rejection reason when a URL was configured but refused.</param>
    /// <returns><c>false</c> only when a configured URL was rejected by validation.</returns>
    public static bool TryCreateTransport(
        McpServerConfig serverConfig,
        out IMcpTransport? transport,
        out string? error)
    {
        transport = null;
        error = null;

        if (!string.IsNullOrWhiteSpace(serverConfig.Url))
        {
            // A transport built here carries whatever headers the caller merged in, which for the
            // auth path includes a resolved provider API key. Validate the scheme at the single
            // seam before any credential can leave the process (#3012).
            var carriesCredentials =
                McpUrlSecurity.HeadersCarryCredentials(serverConfig.Headers)
                || !string.IsNullOrWhiteSpace(serverConfig.Auth);

            if (!McpUrlSecurity.TryValidate(serverConfig.Url, carriesCredentials, out var endpoint, out error))
                return false;

            transport = new HttpSseMcpTransport(endpoint!, serverConfig.Headers?.AsReadOnly());
            return true;
        }

        if (!string.IsNullOrWhiteSpace(serverConfig.Command))
        {
            transport = new StdioMcpTransport(
                serverConfig.Command,
                serverConfig.Args,
                serverConfig.Env,
                serverConfig.WorkingDirectory,
                serverConfig.InheritEnv);
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAllAsync().ConfigureAwait(false);
    }
}
