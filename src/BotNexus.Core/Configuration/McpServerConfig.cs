namespace BotNexus.Core.Configuration;

/// <summary>Transport type for an MCP server connection.</summary>
public enum McpTransportType
{
    /// <summary>Spawns a local process and speaks JSON-RPC 2.0 over stdin/stdout.</summary>
    Stdio,
    /// <summary>HTTP Server-Sent Events endpoint (legacy MCP SSE transport).</summary>
    Sse,
    /// <summary>Streamable HTTP endpoint (new MCP transport).</summary>
    StreamableHttp
}

/// <summary>MCP (Model Context Protocol) server configuration.</summary>
public class McpServerConfig
{
    /// <summary>Friendly name for this server; used as a tool-name prefix.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Transport type. Defaults to <see cref="McpTransportType.Stdio"/> when <see cref="Command"/> is set,
    /// otherwise inferred from <see cref="Url"/>.</summary>
    public McpTransportType? Type { get; set; }

    // ── Stdio transport ──────────────────────────────────────────────────────

    /// <summary>Executable to launch (stdio transport).</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments to pass to <see cref="Command"/> (stdio transport).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Extra environment variables injected into the child process (stdio transport).</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    // ── HTTP/SSE transport ───────────────────────────────────────────────────

    /// <summary>HTTP endpoint URL (SSE or StreamableHttp transport).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Extra HTTP headers (SSE or StreamableHttp transport).</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>Per-tool-call timeout in seconds.</summary>
    public int ToolTimeout { get; set; } = 30;

    /// <summary>
    /// Whitelist of tool names to expose from this server. Use <c>["*"]</c> (default) for all.
    /// An empty list means no tools are exposed.
    /// </summary>
    public List<string> EnabledTools { get; set; } = ["*"];

    /// <summary>Resolves the effective transport type based on set fields.</summary>
    public McpTransportType EffectiveTransport =>
        Type ?? (string.IsNullOrEmpty(Command) ? McpTransportType.Sse : McpTransportType.Stdio);
}
