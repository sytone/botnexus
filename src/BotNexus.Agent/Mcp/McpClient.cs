using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Mcp;

/// <summary>
/// Lightweight JSON-RPC 2.0 client for the Model Context Protocol (MCP).
/// Supports stdio and HTTP/SSE transports.
/// </summary>
public sealed class McpClient : IMcpClient
{
    private readonly McpServerConfig _config;
    private readonly ILogger _logger;

    // Stdio state
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;

    // HTTP state
    private HttpClient? _httpClient;

    private int _nextId;
    private bool _initialized;

    /// <summary>Discovered remote tool schemas, keyed by tool name.</summary>
    public IReadOnlyDictionary<string, JsonObject> RemoteTools { get; private set; } =
        new Dictionary<string, JsonObject>();

    public McpClient(McpServerConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Opens the transport and performs the MCP initialize handshake.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_config.EffectiveTransport == McpTransportType.Stdio)
            await InitializeStdioAsync(cancellationToken).ConfigureAwait(false);
        else
            await InitializeHttpAsync(cancellationToken).ConfigureAwait(false);

        _initialized = true;
    }

    // ── Stdio ────────────────────────────────────────────────────────────────

    private async Task InitializeStdioAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.Command))
            throw new InvalidOperationException($"MCP server '{_config.Name}': Command must be set for stdio transport.");

        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in _config.Args) psi.ArgumentList.Add(arg);
        foreach (var (k, v) in _config.Env) psi.Environment[k] = v;

        _process = new Process { StartInfo = psi };
        _process.Start();

        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;

        _logger.LogDebug("MCP stdio server '{Name}' started (PID {Pid})", _config.Name, _process.Id);

        await SendInitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject?> SendRpcAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (_config.EffectiveTransport == McpTransportType.Stdio)
            return await SendRpcStdioAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        else
            return await SendRpcHttpAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject?> SendRpcStdioAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("Stdio transport not initialized.");

        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null) request["params"] = parameters;

        var json = request.ToJsonString();
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Read response line
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null) return null;

        return JsonNode.Parse(line) as JsonObject;
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private async Task InitializeHttpAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.Url))
            throw new InvalidOperationException($"MCP server '{_config.Name}': Url must be set for HTTP transport.");

        _httpClient = new HttpClient();
        foreach (var (k, v) in _config.Headers)
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

        await SendInitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject?> SendRpcHttpAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (_httpClient is null) throw new InvalidOperationException("HTTP transport not initialized.");

        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null) request["params"] = parameters;

        var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.ToolTimeout + 5));

        var response = await _httpClient.PostAsync(_config.Url, content, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject;
    }

    // ── Protocol ──────────────────────────────────────────────────────────────

    private async Task SendInitializeAsync(CancellationToken cancellationToken)
    {
        var initParams = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "BotNexus",
                ["version"] = "1.0.0"
            }
        };

        var initResponse = await SendRpcAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
        if (initResponse is null)
            throw new InvalidOperationException($"MCP server '{_config.Name}': No response to initialize.");

        _logger.LogInformation("MCP server '{Name}' initialized", _config.Name);

        // Send initialized notification
        var notif = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
        if (_config.EffectiveTransport == McpTransportType.Stdio && _writer is not null)
        {
            await _writer.WriteLineAsync(notif.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Discover tools
        await ListToolsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ListToolsAsync(CancellationToken cancellationToken)
    {
        var response = await SendRpcAsync("tools/list", null, cancellationToken).ConfigureAwait(false);
        if (response is null) return;

        var tools = new Dictionary<string, JsonObject>();
        var toolsArray = response["result"]?["tools"]?.AsArray();
        if (toolsArray is not null)
        {
            foreach (var tool in toolsArray)
            {
                if (tool is JsonObject toolObj && toolObj["name"]?.GetValue<string>() is { } name)
                    tools[name] = toolObj;
            }
        }

        RemoteTools = tools;
        _logger.LogInformation("MCP server '{Name}' exposed {Count} tools: {Tools}",
            _config.Name, tools.Count, string.Join(", ", tools.Keys));
    }

    /// <summary>Calls a remote tool by name with JSON arguments.</summary>
    public async Task<string> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken = default)
    {
        if (!_initialized) throw new InvalidOperationException("McpClient not initialized. Call InitializeAsync first.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.ToolTimeout));

        var parameters = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments
        };

        var response = await SendRpcAsync("tools/call", parameters, cts.Token).ConfigureAwait(false);
        if (response is null) return "Error: No response from MCP server";

        if (response["error"] is { } error)
            return $"Error: {error["message"]?.GetValue<string>() ?? "Unknown MCP error"}";

        // Extract text content from the result
        var result = response["result"];
        var content = result?["content"]?.AsArray();
        if (content is not null)
        {
            var texts = content
                .OfType<JsonObject>()
                .Where(c => c["type"]?.GetValue<string>() == "text")
                .Select(c => c["text"]?.GetValue<string>() ?? string.Empty);
            return string.Join("\n", texts);
        }

        return result?.ToJsonString() ?? "No content";
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill();
            }
            catch { /* ignore */ }
            _process.Dispose();
        }
        _writer?.Dispose();
        _reader?.Dispose();
        _httpClient?.Dispose();
        await ValueTask.CompletedTask;
    }
}
