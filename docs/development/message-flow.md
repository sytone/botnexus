# Message Routing and Session Flow

This document describes how messages flow through BotNexus from client to agent and back, including session lifecycle management.

## Overview

BotNexus uses a **channel-based routing architecture** where messages flow through:

1. **Channel Adapters** (SignalR, Telegram, TUI, etc.) → 
2. **Channel Dispatcher** (IChannelDispatcher) → 
3. **Message Router** (IMessageRouter) → 
4. **Agent Supervisor** (IAgentSupervisor) → 
5. **Agent Handle** (IAgentHandle) → 
6. **Agent Response** → 
7. **Channel Adapter** (broadcast to session group)

## Message Routing Flow

### 1. Client Connection (WebUI Example)

```text
User → SignalR Client → GatewayHub.SubscribeAll()
```

**WebUI Connection Sequence:**

1. Client connects to SignalR hub at `/gateway`
2. Calls `SubscribeAll()` to receive all session metadata
3. Hub joins client to all non-Sealed session groups: `session:{sessionId}`
4. Client receives list of available sessions with metadata

**Key Code:**
- `GatewayHub.SubscribeAll()`: Returns all sessions visible to the connection
- `SignalRChannelAdapter`: Implements `IChannelAdapter` for SignalR broadcasting
- Session groups pattern: `session:{sessionId}` for targeted message delivery

### 2. Sending a Message

```text
Client → SendMessage(agentId, channelType, content) → Auto-Session → Dispatch
```

**SendMessage Flow (New Model):**

1. Client calls `SendMessage(agentId, channelType, content)`
2. Hub calls `ResolveOrCreateSessionAsync(agentId, channelType)`
   - Looks for existing active session for agent + channel
   - Creates new session if none exists (auto-session)
3. Hub subscribes caller to session group (if not already)
4. Hub dispatches inbound message via `IChannelDispatcher`

**Session Creation (Auto-Session on Send):**

```csharp
var session = await ResolveOrCreateSessionAsync(agentId, channelType);
// Sets:
// - SessionId: auto-generated or resolved
// - AgentId: from request
// - ChannelType: from request
// - SessionType: UserAgent
// - Participants: [User: connectionId]
// - Status: Active

await SubscribeInternalAsync(session.SessionId);
await DispatchMessageAsync(agentId, session.SessionId, content, "message");
```

### 3. Message Dispatch

```text
IChannelDispatcher.DispatchAsync(InboundMessage)
```

**Dispatcher Role:**
- Entry point for all inbound messages from any channel
- Creates `InboundMessage` with metadata (channel type, sender ID, session ID)
- Routes to `IMessageRouter` for agent resolution

**InboundMessage Structure:**
```csharp
{
    ChannelType: "signalr",
    SenderId: connectionId,
    ConversationId: sessionId,
    SessionId: sessionId,
    TargetAgentId: agentId,  // Optional explicit target
    Content: "user message",
    Metadata: { messageType: "message" }
}
```

### 4. Agent Resolution

```text
IMessageRouter.ResolveAsync(InboundMessage) → AgentId[]
```

**Resolution Priority:**

1. **Explicit Target**: If `message.TargetAgentId` is set → use that agent
2. **Session Binding**: If `message.SessionId` exists → use session's bound agent
3. **Default Agent**: Use `GatewayOptions.DefaultAgentId`
4. **None**: Return empty list (no route)

**Key Code:**
- `DefaultMessageRouter`: Implements priority-based resolution
- Session binding is persisted in `GatewaySession.AgentId`
- Default agent is configured in `appsettings.json`

### 5. Agent Execution

```text
IAgentSupervisor.GetOrCreateAsync(agentId, sessionId) → IAgentHandle
```

**Supervisor Responsibilities:**

- Manages agent instance lifecycle per (agentId, sessionId) pair
- Uses `IIsolationStrategy` to create agent instances
- Enforces concurrency limits (`MaxConcurrentSessions`)
- Caches running instances (Idle/Running status)
- Coordinates graceful shutdown

**Instance Creation Flow:**

1. Check if instance exists for (agentId, sessionId)
2. If exists and healthy → return existing handle
3. If not exists → delegate to isolation strategy
4. Cache instance and return handle

**Isolation Strategies:**
- **InProcess** (default): Wraps `BotNexus.Agent.Core.Agent` directly
- **Container**: Spawns agent in isolated container
- **Remote**: Connects to remote agent endpoint
- **Sandbox**: Runs agent in sandboxed process

### 6. Agent Prompt and Tool Execution

```text
IAgentHandle.PromptAsync(message) → AgentLoopRunner → Tool Execution → Response
```

**InProcess Isolation Flow:**

1. `InProcessIsolationStrategy.CreateAsync()`:
   - Builds system prompt via `IContextBuilder`
   - Creates workspace path via `IAgentWorkspaceManager`
   - Instantiates tools via `IAgentToolFactory`
   - Creates `BotNexus.Agent.Core.Agent` instance
   - Wraps in `InProcessAgentHandle`

2. `IAgentHandle.PromptAsync(message)`:
   - Appends user message to agent timeline
   - Calls `AgentLoopRunner` (from AgentCore)
   - Streams response events via callback

3. `AgentLoopRunner` execution:
   - Converts messages to LLM context
   - Calls `LlmClient.StreamAsync()`
   - Accumulates streaming response
   - Executes tool calls if requested
   - Repeats until LLM returns `stop_reason: end_turn`

**Tool Execution:**
- `ToolExecutor` runs tools sequentially or parallel (configurable)
- Before/after hooks via `IHookDispatcher`
- Tool policies via `IToolPolicy` (path validation, security checks)
- Results appended as `ToolResultMessage` to timeline

### 7. Response Streaming

```text
AgentLoopRunner → AgentEvent → IChannelAdapter.SendStreamEventAsync()
```

**Event Flow:**

1. `AgentLoopRunner` emits `AgentEvent`s:
   - `MessageStart`: Agent begins response
   - `ThinkingDelta`: Model reasoning (if enabled)
   - `ContentDelta`: Incremental text output
   - `ToolStart`: Tool execution begins
   - `ToolEnd`: Tool execution complete
   - `MessageEnd`: Response complete

2. `InProcessAgentHandle` wraps events as `AgentStreamEvent`

3. `SignalRChannelAdapter.SendStreamEventAsync(conversationId, event)`:
   - Maps event type to SignalR method name
   - Broadcasts to session group: `Clients.Group("session:{sessionId}")`
   - All subscribed clients receive event in real-time

**SignalR Methods:**
- `MessageStart`: Signals response begin
- `ThinkingDelta`: Streams model thinking text
- `ContentDelta`: Streams response content
- `ToolStart`: Tool execution metadata
- `ToolEnd`: Tool result summary
- `MessageEnd`: Signals response complete
- `Error`: Error details

### 8. Session Group Broadcast

```text
SignalR Group: session:{sessionId} → All Subscribed Clients
```

**Group Management:**
- Each session has a SignalR group: `session:{sessionId}`
- Clients join groups via `SubscribeAll()` or `SendMessage()`
- All streaming events broadcast to group
- Supports multi-client collaboration (future)

**WebUI Rendering:**
- Client listens to SignalR events
- Accumulates deltas in session-specific store
- Renders to DOM via `marked.js` (Markdown)
- Sanitizes via `DOMPurify`

## Session Lifecycle

### Session States

```text
Active → Suspended → Sealed
```

**Status Definitions:**

- **Active**: Currently accepting new messages
- **Suspended**: Paused, can be resumed
- **Sealed**: Completed, read-only — terminal state (e.g., old soul sessions, completed cron sessions)

**Visibility Rule:** Any non-Sealed session is visible to clients. `IsInteractive` on the session determines whether the user can send messages.

### Session Types

Sessions are discriminated by `SessionType`:

- **UserAgent**: User ↔ Agent conversation (most common)
- **AgentSelf**: Agent internal reflection
- **AgentSubAgent**: Parent agent ↔ sub-agent
- **AgentAgent**: Peer agent-to-agent via `agent_converse`
- **Soul**: Daily soul session (persistent agent memory)
- **Cron**: Scheduled trigger execution

### Session Creation

**Auto-Creation (SendMessage):**

Finds an existing active UserAgent session for the agent+channel pair, or creates a new one with auto-generated SessionId. Sets session type, channel, and adds the caller as a User participant.

See [GatewayHub.cs](../../src/channels/BotNexus.Channels.SignalR/GatewayHub.cs)

### Session Participants

Sessions track participants via `SessionParticipant`:

```csharp
public record SessionParticipant
{
    public ParticipantType Type { get; set; }  // User, Agent, System
    public string Id { get; set; }             // connectionId or agentId
    public string? Role { get; set; }          // "initiator", "target", etc.
}
```

**Examples:**

- UserAgent session: `[{ Type: User, Id: connectionId }]`
- AgentAgent session: `[{ Type: Agent, Id: initiatorId, Role: "initiator" }, { Type: Agent, Id: targetId, Role: "target" }]`

### Session Persistence

Sessions are persisted via `ISessionStore`:

- **InMemorySessionStore**: Dev/testing (not durable)
- **FileSessionStore**: JSON files in `~/.botnexus/sessions/`
- **SqliteSessionStore**: SQLite database (production default)

**Session Structure:**

```csharp
public class GatewaySession
{
    public SessionId SessionId { get; set; }
    public AgentId AgentId { get; set; }
    public SessionType SessionType { get; set; }
    public SessionStatus Status { get; set; }
    public ChannelKey? ChannelType { get; set; }
    public string? CallerId { get; set; }
    public List<SessionParticipant> Participants { get; set; }
    public List<SessionEntry> History { get; set; }
    public Dictionary<string, object?> Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
```

### Session Visibility and Filtering

**Existence Queries:**

`ISessionStore` supports filtered queries via `ExistenceQuery`:

```csharp
// Get all active UserAgent sessions for an agent
var sessions = await _sessions.ListAsync(agentId, ct);
var userSessions = sessions.Where(s =>
    s.SessionType == SessionType.UserAgent &&
    s.Status == SessionStatus.Active);
```

**SubscribeAll Filtering:**

`ISessionWarmupService.GetAvailableSessionsAsync()` returns sessions visible to client:
- Active or Suspended status
- UserAgent or Soul type (default)
- Not expired
- Configurable filter via `SessionWarmupOptions`

## Channel Adapters

Channel adapters implement `IChannelAdapter` to integrate external communication systems.

### Adapter Interface

```csharp
public interface IChannelAdapter
{
    ChannelKey ChannelType { get; }
    string DisplayName { get; }
    
    // Capabilities
    bool SupportsStreaming { get; }
    bool SupportsSteering { get; }
    bool SupportsFollowUp { get; }
    bool SupportsThinkingDisplay { get; }
    bool SupportsToolDisplay { get; }
    
    // Lifecycle
    Task StartAsync(IChannelDispatcher dispatcher, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    
    // Outbound
    Task SendAsync(OutboundMessage message, CancellationToken ct);
    Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken ct);
}
```

### Built-In Adapters

**SignalRChannelAdapter:**
- Bidirectional real-time communication
- Supports all capabilities (streaming, steering, thinking, tools)
- Uses SignalR groups for session-based broadcast
- Primary adapter for WebUI

**CronChannelAdapter:**
- Used by `CronTrigger` for scheduled execution
- No streaming (batch execution only)
- Creates `SessionType.Cron` sessions
- Results logged to session history

**CrossWorldChannelAdapter:**
- Federation between BotNexus instances
- HTTP-based relay protocol
- No streaming (synchronous exchange)
- Used for agent-to-agent cross-world conversations

### Custom Channel Adapters

External adapters can be implemented for:
- Telegram Bot API
- Discord Bot API
- Slack API
- Terminal UI (TUI)
- SMS/WhatsApp
- Voice interfaces

Adapters are registered in DI and started by `GatewayHost`.

## Deprecated: Direct Session Join

**Legacy Pattern (Deprecated):**

```csharp
// OLD: JoinSession(agentId, sessionId)
await hub.Invoke("JoinSession", "my-agent", null);  // Creates session
await hub.Invoke("Prompt", content);                // Sends message
```

**New Pattern (Current):**

```csharp
// NEW: SubscribeAll + SendMessage
await hub.Invoke("SubscribeAll");                          // Subscribe to all
await hub.Invoke("SendMessage", "my-agent", "signalr", content);  // Auto-session
```

**Rationale:**
- SubscribeAll enables multi-session UI without manual join/leave
- SendMessage auto-creates sessions on demand (simpler client code)
- Channel-centric model supports multi-channel agents
- Backwards compatible (JoinSession still works but deprecated)

## Summary

**Key Architectural Decisions:**

1. **Channel-based routing**: Messages are channel-scoped, not session-scoped initially
2. **Auto-session on send**: Sessions created on first message, not explicit creation
3. **Session group broadcast**: SignalR groups enable multi-client collaboration
4. **Subscribe-all model**: Clients subscribe to all sessions upfront, switch views client-side
5. **Isolation strategies**: Pluggable agent execution (in-process, container, remote)
6. **Session type discrimination**: Different flows for UserAgent, AgentAgent, Soul, Cron
7. **Participant tracking**: Sessions record all participants for visibility and access control

**Performance Characteristics:**

- In-process isolation: <10ms agent startup
- Session lookup: O(1) via dictionary (in-memory) or indexed query (SQLite)
- SignalR broadcast: O(N) where N = clients in session group
- Auto-session overhead: Single DB lookup + potential creation (amortized across messages)

**Scalability Considerations:**

- Session store must support high read volume (every message routes via session)
- SignalR groups scale to ~10K connections per hub (horizontal scaling via backplane)
- Agent instance cache prevents redundant initialization
- Concurrency limits prevent resource exhaustion
