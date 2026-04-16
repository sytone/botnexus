# System Flows

**Purpose:** Key runtime flows showing how components interact to deliver BotNexus functionality.

---

## 1. Message Routing Flow

**Trigger:** User sends a message via any channel (SignalR, Telegram, TUI, etc.)

**Flow:**

```text
Channel → ChannelDispatcher → MessageRouter → AgentSupervisor → AgentHandle → AgentLoopRunner → LlmClient → Provider
```

**Steps:**

1. **Channel receives message** (e.g., SignalR `SendMessage(agentId, channelType, content)`)
2. **Auto-session resolution**: Channel looks for existing Active session or creates new one
3. **Channel adapts to InboundMessage**: `{ ChannelType, SenderId, SessionId, TargetAgentId, Content }`
4. **Dispatcher routes to MessageRouter**
5. **Router resolves target agent**: Explicit target → session binding → default agent
6. **Supervisor gets/creates agent instance** for (agentId, sessionId) pair
7. **Handle prompts agent**: `IAgentHandle.PromptAsync(message)`
8. **AgentLoopRunner orchestrates turn**: LLM call → tool execution → repeat until done
9. **Response streams back** through channel to all session group subscribers

**Diagram:**

```text
┌─────────┐    ┌─────────────┐    ┌────────────┐    ┌────────────┐    ┌──────────┐
│ Channel │───▶│  Dispatcher │───▶│   Router   │───▶│ Supervisor │───▶│  Handle  │
│ Adapter │◀───│             │◀───│            │◀───│            │◀───│          │
└─────────┘    └─────────────┘    └────────────┘    └────────────┘    └──────────┘
     │                                                                       │
     └─────── Broadcast to session group ◀───────────────────────────────────┘
```

**Participants:** `IChannelAdapter`, `IChannelDispatcher`, `IMessageRouter`, `IAgentSupervisor`, `IAgentHandle`, `AgentLoopRunner`

**Output:** Streaming response events broadcast to session group

---

## 2. Agent Execution Flow

**Trigger:** `IAgentSupervisor.GetOrCreateAsync(agentId, sessionId)`

**Flow:**

```text
Supervisor → Check Cache → Isolation Strategy → Create Agent → Wrap in Handle → Cache and Return
```

**Steps:**

1. **Check instance cache** for (agentId, sessionId) key
2. **If exists and healthy** (Idle or Running status) → return cached handle
3. **If not exists**:
   - Load `AgentDescriptor` from registry
   - Check concurrency limit (`MaxConcurrentSessions`)
   - Resolve `IIsolationStrategy` for `ExecutionStrategy`
   - Call `strategy.CreateAsync(descriptor, context, ct)`
4. **Strategy creates agent** (InProcess example):
   - Resolve model from `ModelRegistry`
   - Build system prompt via `IContextBuilder`
   - Create workspace path via `IAgentWorkspaceManager`
   - Instantiate tools via `IAgentToolFactory`
   - Setup hooks via `IHookDispatcher`
   - Create `AgentCore.Agent` instance
   - Wrap in `InProcessAgentHandle`
5. **Cache instance** with status = Idle
6. **Return handle** to caller

**Diagram:**

```text
Supervisor
  │
  ├─ Cache Hit? ──Yes──▶ Return Cached Handle
  │
  └─ No ──▶ IsolationStrategy.CreateAsync()
              │
              ├─ Build System Prompt
              ├─ Create Tools
              ├─ Setup Hooks
              └─ Wrap in IAgentHandle ──▶ Cache & Return
```

**Participants:** `IAgentSupervisor`, `IIsolationStrategy`, `IContextBuilder`, `IAgentToolFactory`, `IHookDispatcher`, `AgentCore.Agent`

**Output:** `IAgentHandle` ready for execution

---

## 3. Agent Loop (Tool Execution Cycle)

**Trigger:** `IAgentHandle.PromptAsync(message)` or `ContinueAsync()`

**Flow:**

```text
Loop: Drain Steering → Convert Context → Call LLM → Accumulate Response → Execute Tools? → Repeat
```

**Steps:**

1. **Drain steering queue**: Inject any mid-turn guidance messages
2. **Convert messages to provider context**: `AgentMessage[]` → `Context` (system prompt + messages + tools)
3. **Call LLM**: `LlmClient.StreamAsync(model, context, options)`
4. **Accumulate streaming response**: `StreamAccumulator` converts SSE events to `AgentEvent`s
5. **If assistant requests tool calls**:
   - Execute tools (sequential or parallel based on `ToolExecutionMode`)
   - Run before/after hooks
   - Append tool results to timeline
   - **Goto step 1** (next loop iteration)
6. **If no tool calls**: Emit `MessageEnd` event and exit loop

**Diagram:**

```text
┌──────────────────────────────────────────────────┐
│                                                  │
│  1. Drain Steering Messages                     │
│       ↓                                          │
│  2. Convert Timeline → Provider Context          │
│       ↓                                          │
│  3. Call LLM (full context sent)                 │
│       ↓                                          │
│  4. Accumulate Streamed Response                 │
│       ↓                                          │
│  5. Tool Calls? ──Yes──▶ Execute Tools ──────────┘
│       │                      ↓
│       No                Append Results
│       ↓                      ↓
│  6. Exit                 (loop again)
└──────────────────────────────────────────────────┘
```

**Key Insight:** Every LLM call sends the **full context** (system prompt + all messages). Providers are stateless — they have no memory of prior calls.

**Participants:** `AgentLoopRunner`, `ContextConverter`, `LlmClient`, `StreamAccumulator`, `ToolExecutor`

**Output:** Final assistant message with no pending tool calls

---

## 4. Session Lifecycle

**States:** Active → Suspended → Sealed

**Transitions:**

| From | To | Trigger |
|------|-----|---------|
| (none) | Active | First message via `SendMessage()` (auto-session) |
| Active | Suspended | User pauses, system timeout |
| Active | Sealed | Cron completes, soul session rotates |
| Suspended | Active | User resumes |
| Suspended | Sealed | User closes, admin action |

**Creation Flow:**

1. **Client sends message** via channel (e.g., WebUI `SendMessage("gateway", "signalr", "Hello")`)
2. **Channel looks for existing Active session** with (agentId, channelType)
3. **If not found**:
   - Generate `SessionId` (auto or soul format)
   - Create `GatewaySession` with `SessionType.UserAgent`, `Status.Active`
   - Add user participant: `{ Type: User, Id: connectionId }`
   - Save to session store
4. **Subscribe caller to session group**: `session:{sessionId}`
5. **Dispatch message** via `IChannelDispatcher`

**Diagram:**

```text
SendMessage(agentId, channelType, content)
  │
  ├─ Existing Active Session? ──Yes──▶ Use It
  │
  └─ No ──▶ Create New Session
              │
              ├─ SessionType: UserAgent
              ├─ Status: Active
              ├─ Participants: [User: connectionId]
              └─ Save & Subscribe to Group
                    │
                    └──▶ Dispatch Message
```

**Participants:** `GatewayHub`, `ISessionStore`, `IChannelDispatcher`

**Output:** Session ready for message dispatch, client subscribed to updates

---

## 5. Channel Dispatch

**Trigger:** Agent sends response or streaming event

**Flow:**

```text
AgentHandle → ChannelAdapter.SendAsync(OutboundMessage) → SignalR Group Broadcast → All Subscribed Clients
```

**Steps:**

1. **Agent emits `AgentEvent`** (e.g., `ContentDelta`, `ToolStart`, `MessageEnd`)
2. **Handle wraps as `AgentStreamEvent`** with sessionId
3. **Channel adapter receives event**: `IChannelAdapter.SendStreamDeltaAsync(conversationId, delta, ct)`
4. **Adapter broadcasts to session group**: `Clients.Group("session:{sessionId}").SendAsync(method, data)`
5. **All clients in group receive event** in real-time

**SignalR Methods:**

- `MessageStart`: Response begins
- `ContentDelta`: Incremental text
- `ThinkingDelta`: Model reasoning (if enabled)
- `ToolStart` / `ToolEnd`: Tool execution lifecycle
- `MessageEnd`: Response complete
- `Error`: Error details

**Diagram:**

```text
AgentLoopRunner
  │ (emits AgentEvent)
  ▼
InProcessAgentHandle
  │ (wraps as AgentStreamEvent)
  ▼
SignalRChannelAdapter
  │ (broadcasts to group)
  ▼
SignalR Hub → Clients.Group("session:{sessionId}")
  │
  └──▶ All Subscribed Clients (WebUI, CLI, etc.)
```

**Participants:** `IAgentHandle`, `IChannelAdapter`, `GatewayHub`, SignalR client

**Output:** Real-time streaming to all session participants

---

## 6. Internal Triggers

### Cron Trigger

**Schedule:** User-defined cron expression (e.g., `"0 8 * * *"`)

**Flow:**

1. **Cron scheduler fires** at scheduled time
2. **CronTrigger creates `InboundMessage`**:
   - `ChannelType: "cron"`
   - `SessionType: Cron`
   - `Content`: heartbeat prompt or task definition
3. **Dispatch to agent** (no streaming, batch execution)
4. **Collect full response**
5. **Seal session** (Cron sessions are one-shot)
6. **Log result** to session history

**Use Cases:** Scheduled reports, daily summaries, health checks, data sync

---

### Soul Trigger

**Schedule:** Daily at configured time (default: 8am)

**Flow:**

1. **Soul scheduler fires** at heartbeat time
2. **Check for existing soul session** with today's date: `soul:{agentId}:{yyyy-MM-dd}`
3. **If previous day's session exists**: Seal it (end-of-day reflection)
4. **Create new soul session** with today's date
5. **Dispatch heartbeat prompt** (e.g., "Reflect on recent events")
6. **Agent processes** and updates long-term memory
7. **Session persists** until tomorrow's heartbeat

**Use Cases:** Daily reflection, long-term memory formation, habit tracking

---

## 7. Agent-to-Agent Communication

### Peer Conversations (`agent_converse`)

**Trigger:** Agent calls `agent_converse` tool with `targetAgentId`

**Flow:**

1. **Caller agent executes tool**: `{ name: "agent_converse", arguments: { agentId: "peer", message: "..." } }`
2. **Gateway validates authorization**: Check `SubAgentIds` whitelist
3. **Create dual sessions**:
   - Source session: `SessionType.AgentAgent`, participants: [caller, target]
   - Target session: (reuse or create)
4. **Dispatch to target agent**
5. **Target responds** (no streaming — synchronous exchange)
6. **Return response** to caller as tool result
7. **Cycle detection**: Track call chain to prevent infinite loops

**Diagram:**

```text
Agent A (caller)
  │
  └─ agent_converse(agentId: "B", message: "...")
       │
       ├─ Validate: B in A.SubAgentIds?
       │
       └─ Create Session: Type=AgentAgent, Participants=[A, B]
            │
            └──▶ Dispatch to Agent B
                   │
                   └──▶ Response ──▶ Return to A as ToolResult
```

---

### Cross-World Federation

**Trigger:** Agent references remote agent via `world:{worldId}:{agentId}` format

**Flow:**

1. **Caller uses world-qualified ID**: `agent_converse(agentId: "world:prod:gateway", ...)`
2. **Gateway detects cross-world pattern** and resolves target world
3. **Create HTTP relay request**:
   - URL: `https://{targetWorld}/api/cross-world/relay`
   - Headers: `X-Cross-World-Key: {sharedSecret}`
   - Body: `{ sourceWorldId, sourceAgentId, targetAgentId, message }`
4. **Target world validates auth** and dispatches locally
5. **Target world returns response** (synchronous)
6. **Gateway returns to caller** as tool result

**Security:** Requires pre-shared key (`X-Cross-World-Key`) configured in both worlds

---

## 8. Prompt Pipeline

**Trigger:** `IContextBuilder.BuildSystemPromptAsync(descriptor, ct)`

**Flow:**

```text
Sections + Contributors → Order by Priority → Compose → System Prompt
```

**Steps:**

1. **Discover context files** from workspace (AGENTS.md, SOUL.md, etc.)
2. **Build `PromptContext`**:
   - WorkspaceDir, ContextFiles, AvailableTools
   - Extensions (skills, MCP servers, etc.)
3. **Order sections by `Order` property**: Identity (100) → Workspace (200) → Tools (300) → ContextFiles (400) → Guidelines (500) → Examples (600)
4. **Add contributor sections** (skills, memory, etc.) at their priority levels
5. **Concatenate all sections** into single string
6. **Inject cache boundary** (if supported): `<!-- BOTNEXUS_CACHE_BOUNDARY -->`
7. **Return final system prompt**

**Sections:**

| Order | Section | Content |
|-------|---------|---------|
| 100 | Identity | Agent role, workspace path, runtime environment |
| 200 | Workspace | Directory structure, file tree |
| 300 | Tools | Available tool descriptions |
| 400 | ContextFiles | AGENTS.md, SOUL.md, README.md, etc. |
| 500 | Guidelines | Best practices, safety rules |
| 600 | Examples | Tool usage examples |

**Participants:** `IContextBuilder`, `PromptPipeline`, `IPromptSection[]`, `IPromptContributor[]`

**Output:** Complete system prompt ready for LLM call

---

## Summary

These flows demonstrate BotNexus's layered architecture in action:

1. **Message routing** shows channel-based dispatch
2. **Agent execution** shows pluggable isolation strategies
3. **Agent loop** shows stateless LLM calls with tool orchestration
4. **Session lifecycle** shows auto-creation and state transitions
5. **Channel dispatch** shows real-time streaming to groups
6. **Triggers** show internal event sources (cron, soul)
7. **Agent-to-agent** shows peer communication and federation
8. **Prompt pipeline** shows modular prompt construction

**For implementation:**

- **[Development Guide](../development/message-flow.md)** — Code-level flow walkthroughs
- **[API Reference](../api-reference.md)** — Endpoint and method documentation
