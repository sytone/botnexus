# Agent Execution Architecture

This document describes how agents are loaded, instantiated, executed, and managed within BotNexus Gateway.

## Overview

Agent execution follows a layered approach:

1. **Agent Registry** (`IAgentRegistry`) → Agent metadata and configuration
2. **Agent Supervisor** (`IAgentSupervisor`) → Instance lifecycle management
3. **Isolation Strategy** (`IIsolationStrategy`) → Agent execution environment
4. **Agent Handle** (`IAgentHandle`) → Execution interface
5. **AgentCore** (`BotNexus.AgentCore.Agent`) → Core agent loop
6. **Agent Loop Runner** (`AgentLoopRunner`) → LLM interaction cycle

## Agent Descriptor Loading

### AgentDescriptor Model

```csharp
public record AgentDescriptor
{
    public AgentId AgentId { get; init; }
    public string DisplayName { get; init; }
    public string? Description { get; init; }
    
    // Model Configuration
    public string ApiProvider { get; init; }
    public string ModelId { get; init; }
    
    // Execution
    public ExecutionStrategy ExecutionStrategy { get; init; }  // InProcess, Container, Remote
    public int MaxConcurrentSessions { get; init; }
    
    // Tools and Capabilities
    public List<string> Tools { get; init; }
    public List<string> SubAgentIds { get; init; }
    
    // Prompts
    public string? SystemPrompt { get; init; }
    public string? HeartbeatPrompt { get; init; }
    
    // Security
    public FileAccessPolicy FileAccess { get; init; }
    
    // Extensions
    public List<ExtensionReference> Extensions { get; init; }
    
    // Soul Configuration
    public SoulAgentConfig? Soul { get; init; }
}
```

### Configuration Sources

**PlatformConfig (Primary):**

```json
{
  "worldId": "my-world",
  "agents": [
    {
      "id": "gateway",
      "displayName": "Gateway Assistant",
      "model": "copilot:gpt-4o",
      "executionStrategy": "in-process",
      "tools": ["read", "write", "edit", "exec", "grep", "glob"],
      "systemPrompt": "You are a helpful coding assistant.",
      "fileAccess": {
        "workspaceRoot": "~/projects",
        "allowedPaths": ["~/projects/**"]
      }
    }
  ]
}
```

**File-based Configuration:**

```json
// ~/.botnexus/agents/my-agent.json
{
  "id": "my-agent",
  "displayName": "My Agent",
  "model": "anthropic:claude-sonnet-4",
  "executionStrategy": "in-process"
}
```

### Configuration Loading

**Loading Pipeline:**

1. `PlatformConfigAgentSource`: Loads from `platform-config.json`
2. `FileAgentConfigurationSource`: Loads from `~/.botnexus/agents/*.json`
3. `IAgentRegistry.Register()`: Validates and registers descriptors
4. `AgentConfigurationHostedService`: Watches for file changes

**Validation:**

- `AgentDescriptorValidator.Validate()`:
  - Required fields: `AgentId`, `DisplayName`, `ApiProvider`, `ModelId`
  - Model exists in `ModelRegistry`
  - Execution strategy is registered
  - Tool names are valid (if `DefaultToolRegistry` is used)
  - File access paths are absolute

## Agent Supervisor

### Instance Management

**IAgentSupervisor Responsibilities:**

- Manages agent instances per (agentId, sessionId) pair
- Creates instances via `IIsolationStrategy`
- Enforces concurrency limits
- Caches healthy instances
- Coordinates shutdown

**Instance Key:**

```csharp
AgentSessionKey = (AgentId, SessionId)
```

Each session gets its own agent instance. This enables:
- Session-specific state isolation
- Concurrent conversations with same agent
- Independent lifecycle per conversation
- Clean memory/resource boundaries

### GetOrCreateAsync Flow

```csharp
async Task<IAgentHandle> GetOrCreateAsync(AgentId agentId, SessionId sessionId, CancellationToken ct)
{
    var key = AgentSessionKey.From(agentId, sessionId);
    
    // Check cache
    lock (_sync)
    {
        if (_instances.TryGetValue(key, out var existing) &&
            existing.Instance.Status is Idle or Running)
            return existing.Handle;
    }
    
    // Check concurrency limit
    var descriptor = _registry.Get(agentId);
    if (descriptor.MaxConcurrentSessions > 0)
    {
        var activeSessions = CountActiveSessionsForAgent(agentId);
        if (activeSessions >= descriptor.MaxConcurrentSessions)
            throw new AgentConcurrencyLimitExceededException(agentId, limit);
    }
    
    // Create via isolation strategy
    var strategy = _strategies[descriptor.ExecutionStrategy];
    var handle = await strategy.CreateAsync(descriptor, context, ct);
    
    // Cache and return
    lock (_sync)
    {
        _instances[key] = (Instance, handle);
    }
    
    return handle;
}
```

**Concurrency Control:**

- `MaxConcurrentSessions = 0`: Unlimited (default)
- `MaxConcurrentSessions = 1`: Single active session (serialized execution)
- `MaxConcurrentSessions = N`: Up to N concurrent sessions

**Instance Status:**

```csharp
public enum AgentInstanceStatus
{
    Idle,      // Created, waiting for prompt
    Running,   // Actively processing a request
    Stopping,  // Shutdown in progress
    Stopped    // Fully terminated
}
```

### Instance Metadata

```csharp
public record AgentInstance
{
    public string InstanceId { get; init; }  // Unique ID for this instance
    public AgentId AgentId { get; init; }
    public SessionId SessionId { get; init; }
    public string IsolationStrategy { get; init; }
    public AgentInstanceStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastActiveAt { get; set; }
}
```

## Isolation Strategies

### InProcessIsolationStrategy (Default)

**Characteristics:**
- Runs agent in Gateway process
- Direct `BotNexus.AgentCore.Agent` instantiation
- No process/container boundaries
- Lowest latency (<10ms startup)
- Shared memory space (trusted agents only)

**Creation Flow:**

```csharp
async Task<IAgentHandle> CreateAsync(AgentDescriptor descriptor, AgentExecutionContext context, CancellationToken ct)
{
    // 1. Resolve model
    var model = _llmClient.Models.GetModel(descriptor.ApiProvider, descriptor.ModelId);
    var apiEndpoint = _authManager.GetApiEndpoint(descriptor.ApiProvider);
    if (!string.IsNullOrWhiteSpace(apiEndpoint))
        model = model with { BaseUrl = apiEndpoint };
    
    // 2. Build system prompt
    var systemPrompt = await _contextBuilder.BuildSystemPromptAsync(descriptor, ct);
    
    // 3. Setup workspace
    var workspacePath = _workspaceManager.GetWorkspacePath(descriptor.AgentId);
    var pathValidator = new DefaultPathValidator(descriptor.FileAccess, workspacePath);
    
    // 4. Create tools
    var workspaceTools = _toolFactory.CreateTools(workspacePath, pathValidator);
    var gatewayTools = CreateGatewayTools(descriptor, context.SessionId);
    var allTools = workspaceTools.Concat(gatewayTools).ToList();
    
    // 5. Load extensions
    var extensionTools = await LoadExtensionToolsAsync(descriptor, workspacePath, ct);
    allTools.AddRange(extensionTools);
    
    // 6. Setup hooks
    var hookDispatcher = CreateHookDispatcher(descriptor, pathValidator);
    
    // 7. Create AgentCore.Agent
    var agent = new Agent(
        model: model,
        systemPrompt: systemPrompt,
        tools: allTools,
        options: new AgentOptions
        {
            BeforeToolCall = hookDispatcher.BeforeToolCallAsync,
            AfterToolCall = hookDispatcher.AfterToolCallAsync,
            ToolExecutionMode = descriptor.ToolExecutionMode ?? ToolExecutionMode.Sequential
        },
        llmClient: _llmClient);
    
    // 8. Wrap in handle
    return new InProcessAgentHandle(agent, descriptor, context);
}
```

### ContainerIsolationStrategy

**Characteristics:**
- Runs agent in Docker container
- Network-isolated execution
- Resource limits (CPU, memory)
- Slower startup (~1-3 seconds)
- Stronger security boundary

**Not fully implemented yet** — placeholder for future container-based execution.

### RemoteIsolationStrategy

**Characteristics:**
- Connects to remote agent endpoint
- Agent runs on different machine/cluster
- HTTP/gRPC communication
- Distributed execution
- Horizontal scaling

**Not fully implemented yet** — placeholder for distributed agent architecture.

### SandboxIsolationStrategy

**Characteristics:**
- Runs agent in sandboxed process
- OS-level isolation (AppDomain/.NET sandbox)
- Limited file system access
- Moderate startup (~100-500ms)

**Not fully implemented yet** — placeholder for process-level sandboxing.

## Agent Handle

### IAgentHandle Interface

```csharp
public interface IAgentHandle : IAsyncDisposable
{
    AgentId AgentId { get; }
    SessionId SessionId { get; }
    
    // Execution
    Task<AgentResponse> PromptAsync(string message, CancellationToken ct = default);
    Task<AgentResponse> ContinueAsync(CancellationToken ct = default);
    Task SteerAsync(string message, CancellationToken ct = default);
    Task FollowUpAsync(string message, CancellationToken ct = default);
    Task AbortAsync(CancellationToken ct = default);
    
    // State
    Task<IReadOnlyList<SessionEntry>> GetHistoryAsync(CancellationToken ct = default);
    Task<AgentHealthResponse> GetHealthAsync(CancellationToken ct = default);
}
```

### InProcessAgentHandle

Wraps `BotNexus.AgentCore.Agent` and bridges to Gateway contracts.

**PromptAsync Implementation:**

```csharp
public async Task<AgentResponse> PromptAsync(string message, CancellationToken ct)
{
    var response = new AgentResponse();
    
    // Subscribe to agent events
    var subscription = _agent.Events.Subscribe(agentEvent =>
    {
        var streamEvent = ConvertToGatewayEvent(agentEvent);
        BroadcastToChannel(streamEvent);  // via IChannelAdapter
        
        if (agentEvent is MessageEndEvent endEvent)
            response.Content = endEvent.Content;
    });
    
    try
    {
        // Call AgentCore
        await _agent.PromptAsync(message, ct);
        return response;
    }
    finally
    {
        subscription.Dispose();
    }
}
```

**Event Conversion:**

```csharp
AgentStreamEvent ConvertToGatewayEvent(AgentEvent coreEvent)
{
    return coreEvent switch
    {
        MessageStartEvent => new AgentStreamEvent { Type = MessageStart },
        TextDeltaEvent delta => new AgentStreamEvent { Type = ContentDelta, Delta = delta.Delta },
        ThinkingDeltaEvent thinking => new AgentStreamEvent { Type = ThinkingDelta, Delta = thinking.Delta },
        ToolCallStartEvent tool => new AgentStreamEvent { Type = ToolStart, ToolName = tool.Name },
        ToolCallEndEvent tool => new AgentStreamEvent { Type = ToolEnd, ToolName = tool.Name },
        MessageEndEvent => new AgentStreamEvent { Type = MessageEnd },
        ErrorEvent error => new AgentStreamEvent { Type = Error, ErrorMessage = error.Message },
        _ => null
    };
}
```

## Agent Loop Runner

### Core Loop (AgentCore)

The `AgentLoopRunner` implements the agent-tool execution cycle:

```
1. Drain pending steering messages
2. Convert agent messages to LLM context
3. Call LlmClient.StreamAsync()
4. Accumulate streaming response
5. If tool calls requested:
   a. Execute tools (sequential or parallel)
   b. Append tool results to timeline
   c. Goto 1
6. Return final response
```

**Detailed Flow:**

```csharp
async Task RunLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // 1. Drain steering queue
        while (_pendingQueue.TryDequeueSteer(out var steerMessage))
        {
            _state.Messages.Add(steerMessage);
        }
        
        // 2. Convert to LLM context
        var context = _converter.ToProviderContext(_state);
        
        // 3. Stream LLM response
        var stream = await _llmClient.StreamAsync(context, ct);
        
        // 4. Accumulate response
        var accumulator = new StreamAccumulator(_eventEmitter);
        var message = await accumulator.AccumulateAsync(stream, ct);
        _state.Messages.Add(message);
        
        // 5. Execute tools if requested
        if (message.ToolCalls.Any())
        {
            var results = await _toolExecutor.ExecuteAsync(
                message.ToolCalls,
                _state.Tools,
                _options.BeforeToolCall,
                _options.AfterToolCall,
                ct);
            
            _state.Messages.Add(new ToolResultMessage { Results = results });
            continue;  // Next loop iteration
        }
        
        // 6. Done
        break;
    }
}
```

### Tool Execution

**Sequential Execution (Default):**

```csharp
foreach (var toolCall in toolCalls)
{
    var tool = FindTool(toolCall.Name);
    var result = await tool.ExecuteAsync(toolCall.Id, toolCall.Arguments, ct);
    results.Add(result);
}
```

**Parallel Execution:**

```csharp
var tasks = toolCalls.Select(async toolCall =>
{
    var tool = FindTool(toolCall.Name);
    return await tool.ExecuteAsync(toolCall.Id, toolCall.Arguments, ct);
});
var results = await Task.WhenAll(tasks);
```

**Hook Execution:**

```csharp
// Before tool call
var beforeResult = await BeforeToolCallAsync(new BeforeToolCallContext
{
    ToolName = toolCall.Name,
    Arguments = toolCall.Arguments
});

if (beforeResult.Action == HookAction.Block)
    return new AgentToolResult([new AgentToolContent(Text, beforeResult.BlockMessage)]);

// Execute tool
var result = await tool.ExecuteAsync(...);

// After tool call
var afterResult = await AfterToolCallAsync(new AfterToolCallContext
{
    ToolName = toolCall.Name,
    Result = result
});

return afterResult.ModifiedResult ?? result;
```

## Tool Registry

### Default Tool Registry

**Built-in Tools:**

- `read`: Read files/directories
- `write`: Write complete files
- `edit`: Surgical string replacement edits
- `exec`: Execute shell commands (bash/PowerShell)
- `grep`: Regex search across files
- `glob`: File pattern matching

**Gateway Tools:**

- `session`: Session management (save, list, archive)
- `agent_converse`: Peer agent conversations
- `subagent_spawn`: Spawn task-specific sub-agents
- `subagent_list`: List running sub-agents
- `subagent_manage`: Control sub-agent lifecycle
- `file_watcher`: Watch files for changes
- `delay`: Scheduled delays/reminders
- `cron`: Register cron jobs

**Extension Tools:**

- Loaded from `IExtension` implementations
- MCP tools (via `BotNexus.Extensions.Mcp`)
- Web tools (via `BotNexus.Extensions.WebTools`)
- Skill tools (via `BotNexus.Extensions.Skills`)
- Memory tools (via `BotNexus.Memory`)

### Tool Factory

```csharp
public interface IAgentToolFactory
{
    IReadOnlyList<IAgentTool> CreateTools(
        string workspacePath,
        IPathValidator pathValidator);
}
```

**DefaultAgentToolFactory:**

```csharp
public IReadOnlyList<IAgentTool> CreateTools(string workspacePath, IPathValidator pathValidator)
{
    return
    [
        new ReadTool(workspacePath, pathValidator),
        new WriteTool(workspacePath, pathValidator),
        new EditTool(workspacePath, pathValidator),
        new ShellTool(workspacePath, pathValidator),
        new GrepTool(workspacePath),
        new GlobTool(workspacePath)
    ];
}
```

## Workspace and Context

### Workspace Management

**IAgentWorkspaceManager:**

```csharp
public interface IAgentWorkspaceManager
{
    string GetWorkspacePath(AgentId agentId);
    Task EnsureWorkspaceExistsAsync(AgentId agentId, CancellationToken ct);
}
```

**FileAgentWorkspaceManager (Default):**

- Creates workspace directory: `~/.botnexus/workspaces/{agentId}/`
- Isolates file operations per agent
- Supports custom workspace roots via `FileAccessPolicy`

### System Prompt Building

**IContextBuilder:**

```csharp
public interface IContextBuilder
{
    Task<string> BuildSystemPromptAsync(
        AgentDescriptor descriptor,
        CancellationToken ct);
}
```

**WorkspaceContextBuilder:**

1. Gather context files from workspace
2. Build prompt params (workspace dir, tools, timezone, etc.)
3. Call `SystemPromptBuilder.Build(params)`
4. Return final system prompt

**SystemPromptBuilder (BotNexus.Prompts):**

Uses `PromptPipeline` to compose sections:
- Identity and role
- Workspace and file structure
- Available tools
- Context files (codebase docs)
- Extensions and skills
- Runtime environment
- Guidelines and examples

## Security and Validation

### Path Validation

**IPathValidator:**

```csharp
public interface IPathValidator
{
    bool IsPathAllowed(string path);
    string NormalizePath(string path);
}
```

**DefaultPathValidator:**

- Checks paths against `FileAccessPolicy`
- Validates absolute paths
- Prevents directory traversal
- Enforces workspace boundaries

### Tool Policy

**IToolPolicy:**

```csharp
public interface IToolPolicy
{
    Task<ToolPolicyResult> EvaluateAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct);
}
```

**DefaultToolPolicyProvider:**

- Enforces path restrictions on file tools
- Validates shell command safety
- Blocks dangerous operations (configurable)

### Hook Dispatcher

**IHookDispatcher:**

Coordinates multiple hook handlers:

```csharp
public async Task<BeforeToolCallResult> BeforeToolCallAsync(BeforeToolCallContext context)
{
    foreach (var handler in _handlers)
    {
        var result = await handler.BeforeAsync(context);
        if (result.Action == HookAction.Block)
            return result;
    }
    return BeforeToolCallResult.Allow();
}
```

**Built-in Handlers:**

- `ToolPolicyHookHandler`: Enforces tool policies
- (Future) `AuditHookHandler`: Logs all tool calls
- (Future) `RateLimitHookHandler`: Throttles expensive operations

## Performance Characteristics

**In-Process Agent Startup:**
- Cold start: ~10-50ms (model lookup, workspace setup, tool creation)
- Warm instance reuse: <1ms (cached handle lookup)

**LLM Latency:**
- First token: 200-1000ms (depends on provider and model)
- Streaming: 20-100 tokens/second (provider-dependent)

**Tool Execution:**
- File tools: <1-10ms (local I/O)
- Shell tools: 10ms-60s (depends on command)
- Agent converse: 500ms-30s (depends on peer agent latency)

**Memory Usage:**
- Agent instance: ~5-20MB (depends on prompt size and message history)
- Concurrency scaling: Linear per session (isolated instances)

## Summary

**Key Design Principles:**

1. **Isolation per session**: Each (agent, session) pair gets its own instance
2. **Pluggable execution**: Isolation strategies enable different deployment models
3. **Hook-based extension**: Before/after tool hooks enable security and audit
4. **Stream-first design**: All LLM interactions stream events to clients
5. **Workspace isolation**: File tools operate in agent-specific workspace directories
6. **Lazy instantiation**: Agents created on-demand, not at startup
7. **Concurrency control**: Configurable limits prevent resource exhaustion

**Future Enhancements:**

- Container-based isolation for untrusted agents
- Remote agent execution for distributed deployments
- Agent instance pooling for faster cold starts
- Advanced tool policies (rate limiting, cost controls)
- Multi-turn tool execution (tool → tool chains without LLM round-trip)
