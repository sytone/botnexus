# Research: Session Lifecycle Fragmentation

## Code Analysis — 2026-04-15

### Files Examined

| File | LOC | Role |
|------|-----|------|
| `GatewayHost.cs` | 607 | Central dispatch — the "golden path" with full lifecycle |
| `GatewayHub.cs` | ~450 | SignalR hub — delegates to dispatcher for messages, duplicates session setup for join |
| `CronTrigger.cs` | ~55 | Cron trigger — inline session lifecycle, well-behaved |
| `SoulTrigger.cs` | ~160 | Soul trigger — inline session lifecycle, includes day-rollover sealing |
| `ChatController.cs` | ~170 | REST chat — minimal session lifecycle, misses participants/compaction |
| `AgentConversationService.cs` | ~290 | Agent-to-agent — full inline lifecycle with multi-turn loop |
| `CrossWorldFederationController.cs` | ~110 | Cross-world relay — full inline lifecycle |
| `DefaultSubAgentManager.cs` | ~400 | Sub-agent orchestrator — **no session store interaction at all** |
| `DefaultAgentCommunicator.cs` | ~230 | Sync sub/cross-agent calls — **no session store interaction** |
| `DefaultAgentSupervisor.cs` | ~225 | Agent handle lifecycle — reads from session store for history, never writes |
| `SessionStoreBase.cs` | ~110 | Base session store — `InferSessionType` from SessionId patterns |
| `SessionId.cs` | ~100 | Value object — `IsSubAgent`, `ForSubAgent`, etc. |

### Key Discovery: Supervisor ≠ Session Store

The codebase has two parallel concepts that are easily confused:

1. **`IAgentSupervisor.GetOrCreateAsync(agentId, sessionId)`** — Creates an in-memory **agent handle** (runtime execution context). Does NOT persist anything.

2. **`ISessionStore.GetOrCreateAsync(sessionId, agentId)`** — Creates a persistent **session record** (conversation history, metadata). This is what appears in the API and WebUI.

Every path calls (1). Only some paths call (2). The sub-agent and communicator paths skip (2) entirely.

### The 4 Categories of Session Lifecycle

**Category A: Full lifecycle (GatewayHost only)**
- Session store + supervisor
- Status guards, compaction, telemetry, streaming, error handling
- 230+ lines of orchestration logic

**Category B: Adequate lifecycle (Cron, Soul, AgentConv, CrossWorld)**
- Session store + supervisor
- History recording, type/participant setup
- Missing: compaction, telemetry, status guards

**Category C: Minimal lifecycle (ChatController, GatewayHub.JoinSession)**
- Session store + supervisor
- History recording (ChatCtrl) or session setup (Hub.Join)
- Missing: most lifecycle steps

**Category D: No lifecycle (SubAgentManager, AgentCommunicator)**
- Supervisor only — no session store at all
- Sessions are invisible, ephemeral, and unrecoverable

### Session Store Configuration

Gateway uses **SQLite** session store:
```json
{
  "sessionStore": {
    "type": "Sqlite",
    "connectionString": "Data Source=C:\\Users\\jobullen\\.botnexus\\sessions.db"
  }
}
```

21 sessions in the store — all `user-agent` or `cron`. Zero `agent-subagent`.

### GatewayHub Delegation Pattern

`GatewayHub.SendMessage()` correctly delegates to `IChannelDispatcher.DispatchAsync()` which routes to `GatewayHost.ProcessInboundMessageAsync()`. This means the Hub doesn't need its own lifecycle — it gets it from P1. This is the correct pattern that other paths should follow (or use a shared service).

However, `GatewayHub.JoinSession()` (deprecated) still has its own session setup code that duplicates P1.

### DefaultAgentSupervisor History Loading

`DefaultAgentSupervisor.CreateEntryAsync()` (line 175-225) loads session history from the store via `_sessionStore.GetAsync(sessionId)`. This means:
- If a session exists in the store, the agent gets its history on creation
- If a session was never stored (sub-agents), the agent starts fresh every time
- This is why sub-agent sessions can't resume after gateway restart

### SessionId Conventions

```
user-agent:   {guid}
cron:         cron:{timestamp}:{guid}  
soul:         soul:{agentId}:{date}
sub-agent:    {parentSessionId}::subagent::{guid}
agent-conv:   {initiatorId}::agent::{targetId}::{guid}
cross-agent:  {sourceId}::cross::{targetId}
```

`SessionStoreBase.InferSessionType()` correctly maps these patterns to `SessionType` values. The infrastructure for sub-agent sessions is ready — the data just never gets there.
