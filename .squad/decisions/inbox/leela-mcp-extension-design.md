# MCP Extension Design for BotNexus

**Decision Date:** 2026-04-07  
**Decided By:** Leela (Lead/Architect)  
**Status:** Proposed

## 1. Architecture Overview

### What is MCP?

Model Context Protocol (MCP) is an open standard that enables AI applications to connect to external tool servers. MCP servers expose:
- **Tools**: Executable functions (e.g., database queries, API calls)
- **Resources**: Data sources (e.g., file contents, API responses)
- **Prompts**: Reusable templates for LLM interactions

### How MCP Fits Into BotNexus

BotNexus uses an extension model where extensions are discovered via `botnexus-extension.json` manifests and loaded into isolated `AssemblyLoadContext` instances. The MCP extension follows this pattern:

```
extensions/
└── mcp/
    └── BotNexus.Extensions.Mcp/
        ├── botnexus-extension.json
        ├── BotNexus.Extensions.Mcp.dll
        └── (dependencies)
```

**Key integration point:** The MCP extension creates `IAgentTool` implementations dynamically at agent session start, wrapping MCP server tools so they appear as native BotNexus tools.

### High-Level Data Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                         BotNexus Agent Session                       │
├─────────────────────────────────────────────────────────────────────┤
│  Agent calls tool "github_search_repositories"                      │
│       ↓                                                             │
│  ToolRegistry resolves to McpBridgedTool                           │
│       ↓                                                             │
│  McpBridgedTool.ExecuteAsync(...)                                  │
│       ↓                                                             │
│  McpClient.CallToolAsync("search_repositories", args)              │
│       ↓                                                             │
│  JSON-RPC over stdio or HTTP                                       │
│       ↓                                                             │
│  MCP Server (e.g., @modelcontextprotocol/server-github)            │
└─────────────────────────────────────────────────────────────────────┘
```

## 2. Component Breakdown

### Core Interfaces

```csharp
/// <summary>
/// Factory for creating MCP clients from configuration.
/// </summary>
public interface IMcpClientFactory
{
    Task<IMcpClient> CreateAsync(McpServerConfig config, CancellationToken ct);
}

/// <summary>
/// Represents a connection to an MCP server.
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    string ServerId { get; }
    McpServerCapabilities Capabilities { get; }
    
    Task InitializeAsync(CancellationToken ct);
    Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct);
    Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken ct);
    Task<McpToolResult> CallToolAsync(string name, JsonElement args, CancellationToken ct);
    Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct);
}

/// <summary>
/// Manages MCP server connections for an agent session.
/// </summary>
public interface IMcpSessionManager : IAsyncDisposable
{
    Task<IReadOnlyList<IAgentTool>> InitializeServersAsync(
        McpExtensionConfig config, 
        CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
}
```

### Extension Entry Point

```csharp
/// <summary>
/// Hook handler that initializes MCP servers when an agent session starts.
/// </summary>
public sealed class McpSessionInitHookHandler 
    : IHookHandler<BeforePromptBuildEvent, BeforePromptBuildResult>
{
    // On first prompt build, initialize MCP servers from agent's ExtensionConfig
}
```

### Component Layout

```
BotNexus.Extensions.Mcp/
├── McpExtensionConfig.cs        # Configuration model
├── Transport/
│   ├── IMcpTransport.cs         # Transport abstraction
│   ├── StdioMcpTransport.cs     # stdio subprocess transport
│   └── HttpSseMcpTransport.cs   # HTTP/SSE transport
├── Client/
│   ├── McpClient.cs             # JSON-RPC client implementation
│   ├── McpClientFactory.cs      # Creates clients from config
│   └── McpProtocol.cs           # JSON-RPC message types
├── Tools/
│   ├── McpBridgedTool.cs        # IAgentTool wrapper for MCP tools
│   └── McpToolFactory.cs        # Creates bridged tools
├── Session/
│   ├── McpSessionManager.cs     # Per-session server lifecycle
│   └── McpSessionInitHookHandler.cs # Hook integration
└── botnexus-extension.json
```

## 3. Transport Layer

MCP defines two transport mechanisms. We implement both.

### stdio Transport

The client spawns the MCP server as a subprocess and communicates via stdin/stdout.

```csharp
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    
    public async Task SendAsync(JsonRpcMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync();
    }
    
    public async Task<JsonRpcMessage> ReceiveAsync(CancellationToken ct)
    {
        var line = await _reader.ReadLineAsync(ct);
        return JsonSerializer.Deserialize<JsonRpcMessage>(line);
    }
}
```

**Process Management:**
- Use `ProcessStartInfo` with `RedirectStandardInput/Output/Error`
- Inherit environment variables, merge with config-specified `env`
- Set working directory if specified
- Kill process tree on dispose

### HTTP/SSE Transport (Streamable HTTP)

For remote MCP servers that use HTTP endpoints.

```csharp
public sealed class HttpSseMcpTransport : IMcpTransport
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private string? _sessionId;
    
    public async Task SendAsync(JsonRpcMessage message, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(message),
            Headers = {
                { "Accept", "application/json, text/event-stream" }
            }
        };
        
        if (_sessionId is not null)
            request.Headers.Add("Mcp-Session-Id", _sessionId);
            
        var response = await _httpClient.SendAsync(request, ct);
        // Handle SSE or JSON response
    }
}
```

**Session Management:**
- Store `Mcp-Session-Id` from `InitializeResult` response
- Include in all subsequent requests
- Handle 404 by re-initializing

## 4. Tool Bridging

MCP tools become `IAgentTool` instances that agents can call natively.

### McpBridgedTool

```csharp
public sealed class McpBridgedTool : IAgentTool
{
    private readonly IMcpClient _client;
    private readonly McpToolDefinition _definition;
    
    // Prefix tool names to avoid collisions: "github_search_repositories"
    public string Name => $"{_client.ServerId}_{_definition.Name}";
    
    public string Label => _definition.Name;
    
    public Tool Definition => new(
        Name,
        _definition.Description ?? "",
        _definition.InputSchema);
    
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var result = await _client.CallToolAsync(
            _definition.Name, 
            JsonSerializer.SerializeToElement(arguments), 
            ct);
        
        return ConvertToAgentToolResult(result);
    }
}
```

### Tool Name Prefixing

MCP servers may expose tools with common names like `search` or `read`. To prevent collisions:
- Prefix all MCP tool names with their server ID: `github_search_repositories`
- The original tool name is preserved in the `Label` property
- Agents reference tools by the prefixed name

### Schema Translation

MCP uses JSON Schema for tool parameters. BotNexus `Tool.Parameters` is a `JsonElement`, so we can pass through directly without translation.

## 5. Resource Bridging

MCP resources are data sources that provide context. Options:

**Option A: Resource Tool**
Expose a `{serverId}_read_resource` tool that agents can call to read MCP resources.

**Option B: Context Injection (via Hook)**
During prompt build, inject relevant resource contents into the system prompt.

**Recommendation:** Start with Option A (Resource Tool) — it's simpler and gives agents explicit control. Option B can be added later as an optimization.

```csharp
public sealed class McpResourceTool : IAgentTool
{
    public string Name => $"{_serverId}_read_resource";
    
    public Tool Definition => new(Name, "Read a resource from the MCP server", ...);
    
    public async Task<AgentToolResult> ExecuteAsync(...)
    {
        var uri = arguments["uri"]?.ToString();
        var content = await _client.ReadResourceAsync(uri, ct);
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, content.Text)]);
    }
}
```

## 6. Configuration Model

### Per-Agent Extension Config

Agents configure MCP servers in their `extensions` block:

```json
{
  "agentId": "my-agent",
  "extensions": {
    "botnexus-mcp": {
      "servers": {
        "github": {
          "command": "npx",
          "args": ["-y", "@modelcontextprotocol/server-github"],
          "env": {
            "GITHUB_TOKEN": "${env:GITHUB_TOKEN}"
          }
        },
        "filesystem": {
          "url": "http://localhost:3000/mcp"
        }
      },
      "toolPrefix": true,
      "resourceTools": true
    }
  }
}
```

### Config Classes

```csharp
public sealed class McpExtensionConfig
{
    /// <summary>MCP servers to connect to, keyed by server ID.</summary>
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new();
    
    /// <summary>Whether to prefix tool names with server ID. Default: true.</summary>
    public bool ToolPrefix { get; set; } = true;
    
    /// <summary>Whether to expose resource read tools. Default: true.</summary>
    public bool ResourceTools { get; set; } = true;
}

public sealed class McpServerConfig
{
    // stdio transport
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? WorkingDirectory { get; set; }
    
    // HTTP transport
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    
    /// <summary>Timeout for initialization in milliseconds. Default: 30000.</summary>
    public int InitTimeoutMs { get; set; } = 30_000;
    
    /// <summary>Timeout for tool calls in milliseconds. Default: 60000.</summary>
    public int CallTimeoutMs { get; set; } = 60_000;
}
```

### Environment Variable Substitution

Support `${env:VAR_NAME}` syntax for secrets:
- Resolved at server start time
- Prevents secrets from being stored in config files
- Pattern: `${env:NAME}` or `${env:NAME:-default}`

## 7. Lifecycle Management

### Server Start

MCP servers start when an agent session begins:

1. `InProcessIsolationStrategy.CreateAsync` is called
2. Hook handler reads `ExtensionConfig["botnexus-mcp"]`
3. `McpSessionManager.InitializeServersAsync` spawns/connects to servers
4. Each server goes through MCP initialization handshake
5. Tools are listed and wrapped as `McpBridgedTool` instances
6. Bridged tools are returned and added to the agent's tool list

### Server Stop

MCP servers stop when the agent session ends:

1. `IAgentHandle.DisposeAsync` is called
2. `McpSessionManager.ShutdownAsync` is invoked
3. For stdio: Send graceful shutdown, wait briefly, then kill process tree
4. For HTTP: Send `DELETE` to endpoint with session ID (optional)
5. Dispose all transports and clients

### Restart/Reconnect

For resilience:
- **stdio:** If process exits unexpectedly, optionally restart on next tool call
- **HTTP:** If connection fails, retry with exponential backoff
- Configure via `maxRetries` and `retryDelayMs` in server config

## 8. Error Handling

### Transport Errors

```csharp
public abstract class McpTransportException : Exception
{
    public string ServerId { get; }
}

public class McpConnectionFailedException : McpTransportException { }
public class McpTimeoutException : McpTransportException { }
public class McpProcessExitedException : McpTransportException 
{
    public int ExitCode { get; }
}
```

### Tool Call Errors

MCP tool calls can fail in several ways:

1. **Transport failure:** Connection lost, process died
   - Return error tool result with connection error message
   - Optionally trigger reconnect

2. **JSON-RPC error:** Server returns error response
   - Return error tool result with MCP error message and code

3. **Timeout:** Server doesn't respond in time
   - Return error tool result indicating timeout
   - Don't kill the server (it may be doing valid work)

4. **Invalid response:** Server returns malformed data
   - Return error tool result with parse error

### Error Result Format

```csharp
var errorResult = new AgentToolResult(
    [new AgentToolContent(AgentToolContentType.Text, $"MCP error: {message}")],
    details: new McpToolCallDetails(
        ServerId: serverId,
        ToolName: toolName,
        Error: errorCode,
        ErrorMessage: message
    ));
```

## 9. Security

### Tool Policy Integration

MCP tools must respect the existing tool policy system:

```csharp
public sealed class McpToolPolicyHookHandler 
    : IHookHandler<BeforeToolCallEvent, BeforeToolCallResult>
{
    private readonly IToolPolicyProvider _policyProvider;
    
    public async ValueTask<BeforeToolCallResult?> HandleAsync(
        BeforeToolCallEvent @event, 
        CancellationToken ct)
    {
        // Check if this is an MCP tool (has server prefix)
        if (!IsMcpTool(@event.ToolName))
            return null;
            
        var riskLevel = _policyProvider.GetRiskLevel(@event.ToolName);
        if (riskLevel == ToolRiskLevel.Dangerous)
        {
            // Delegate to approval system
        }
        
        return null;
    }
}
```

### Default Policy for MCP Tools

| Tool Pattern | Risk Level | Requires Approval |
|--------------|------------|-------------------|
| `*_read_*`, `*_list_*`, `*_search_*` | Safe | No |
| `*_write_*`, `*_create_*`, `*_update_*`, `*_delete_*` | Moderate | No |
| `*_exec*`, `*_run*`, `*_shell*` | Dangerous | Yes |

### Server Sandboxing

For stdio servers:
- **Do not** run as elevated user
- Consider process isolation (future: container support)
- Limit environment variables passed through

For HTTP servers:
- Validate TLS certificates (unless localhost)
- Respect authentication requirements
- Don't store secrets in config files

## 10. Testing Strategy

### Unit Tests

| Component | Test Focus |
|-----------|------------|
| `McpProtocol` | JSON-RPC serialization/deserialization |
| `StdioMcpTransport` | Message framing, newline handling |
| `HttpSseMcpTransport` | Request formatting, session headers |
| `McpClient` | Initialization handshake, tool listing |
| `McpBridgedTool` | Argument translation, result mapping |
| `McpExtensionConfig` | Config parsing, env var substitution |

### Integration Tests

| Scenario | Approach |
|----------|----------|
| stdio server lifecycle | Spawn real `echo` or test server, verify tool calls |
| HTTP server lifecycle | Use in-memory test server with SSE support |
| Tool policy enforcement | Configure dangerous tool, verify approval flow |
| Error handling | Kill server mid-call, verify graceful degradation |

### Test Fixtures

Create a minimal test MCP server in C#:

```csharp
public sealed class TestMcpServer
{
    public static async Task Main()
    {
        // Read JSON-RPC from stdin, write to stdout
        // Support: initialize, tools/list, tools/call
    }
}
```

## 11. Implementation Plan

### Phase 1: Core Infrastructure (Farnsworth)

**Dependencies:** None  
**Estimated effort:** 3-5 days

1. Create extension project structure
2. Implement `McpExtensionConfig` and parsing
3. Implement `McpProtocol` (JSON-RPC types)
4. Implement `StdioMcpTransport`
5. Implement `McpClient` with initialization and `tools/list`
6. Unit tests for all components

**Deliverable:** Extension that can connect to stdio MCP servers and list tools.

### Phase 2: Tool Bridging (Farnsworth)

**Dependencies:** Phase 1  
**Estimated effort:** 2-3 days

1. Implement `McpBridgedTool`
2. Implement `McpToolFactory`
3. Implement `tools/call` in `McpClient`
4. Integration with `InProcessIsolationStrategy`
5. End-to-end test with real MCP server (e.g., filesystem)

**Deliverable:** Agents can call MCP tools.

### Phase 3: HTTP Transport (Fry)

**Dependencies:** Phase 1  
**Estimated effort:** 2-3 days

1. Implement `HttpSseMcpTransport`
2. SSE response parsing
3. Session management (`Mcp-Session-Id`)
4. Tests with mock HTTP server

**Deliverable:** Extension supports remote MCP servers.

### Phase 4: Lifecycle & Error Handling (Farnsworth)

**Dependencies:** Phase 2, Phase 3  
**Estimated effort:** 2-3 days

1. Implement `McpSessionManager`
2. Server start/stop/restart logic
3. Error handling and timeout management
4. Implement `McpSessionInitHookHandler`
5. Integration tests for lifecycle scenarios

**Deliverable:** Robust server lifecycle management.

### Phase 5: Security Integration (Bender)

**Dependencies:** Phase 4  
**Estimated effort:** 1-2 days

1. Implement `McpToolPolicyHookHandler`
2. Default policy configuration
3. Environment variable substitution
4. Security documentation

**Deliverable:** MCP tools respect tool policies.

### Phase 6: Resources (Optional, Fry)

**Dependencies:** Phase 2  
**Estimated effort:** 1-2 days

1. Implement `resources/list` in `McpClient`
2. Implement `resources/read` in `McpClient`
3. Implement `McpResourceTool`
4. Tests

**Deliverable:** Agents can read MCP resources.

## 12. Team Assignment

| Phase | Owner | Reviewer |
|-------|-------|----------|
| Phase 1: Core Infrastructure | Farnsworth | Leela |
| Phase 2: Tool Bridging | Farnsworth | Leela |
| Phase 3: HTTP Transport | Fry | Farnsworth |
| Phase 4: Lifecycle | Farnsworth | Leela |
| Phase 5: Security | Bender | Leela |
| Phase 6: Resources | Fry | Farnsworth |
| Documentation | Amy | Leela |
| Test coverage review | Hermes | Leela |

## Open Questions

1. **Prompt injection via MCP tools?** — Should we sanitize tool results from MCP servers? Current approach: treat MCP servers as trusted (they're explicitly configured by the admin).

2. **Dynamic tool discovery?** — MCP supports `notifications/tools/list_changed`. Should we re-list tools when this fires? Defer to future enhancement.

3. **MCP Prompts primitive?** — MCP servers can expose prompt templates. Should we bridge these? Defer — prompts are less common than tools.

4. **Sampling support?** — MCP servers can request LLM completions via `sampling/create`. This inverts the client-server relationship. Defer to future phase.

## References

- [MCP Specification](https://modelcontextprotocol.io/specification/latest)
- [MCP Architecture Concepts](https://modelcontextprotocol.io/docs/concepts/architecture)
- [BotNexus Extension Loader](src/gateway/BotNexus.Gateway/Extensions/AssemblyLoadContextExtensionLoader.cs)
- [IAgentTool Interface](src/agent/BotNexus.AgentCore/Tools/IAgentTool.cs)
- [OpenClaw MCP Implementation](reference — TypeScript implementation for patterns)
