# Research: Eager Session Rehydration

See detailed gateway source analysis in Nova's workspace:
`C:\Users\jobullen\.botnexus\agents\nova\workspace\research-eager-rehydration.md`

## TL;DR

Sessions survive restarts (SQLite), but the agent never sees the history. Three gaps:

1. **No session-to-agent context bridge** — `InProcessIsolationStrategy.CreateAsync()` builds agents with fresh system prompt + empty conversation. Session `History` (loaded from SQLite) is never injected.
2. **No startup rehydration** — `GatewayHost.ExecuteAsync()` starts channels and waits. Zero session work. Sessions loaded lazily on first client message.
3. **No cron session discrimination** — No `session_type` column. Rehydration query could pick up heartbeat session instead of real conversation.

## Key Source Paths

| File | Role |
|------|------|
| `GatewayHost.cs` | Startup + message processing (no session preload) |
| `InProcessIsolationStrategy.cs` | Agent handle creation (no history injection) |
| `SqliteSessionStore.cs` | Persistent store (lazy cache, compaction-aware loading) |
| `GatewayHub.cs` → `JoinSession()` | Client connection (returns isResumed but doesn't bridge to agent) |
| `DefaultAgentSupervisor.cs` | Handle cache (purely in-memory, lost on restart) |
| `LlmSessionCompactor.cs` | Compaction logic (summary stored as SessionEntry with IsCompactionSummary=true) |

## Existing Session Load Behavior

On session load from SQLite, the store already slices from last compaction forward:
```csharp
var lastCompactionIndex = entries.FindLastIndex(e => e.IsCompactionSummary);
if (lastCompactionIndex >= 0)
    entries = entries.GetRange(lastCompactionIndex, entries.Count - lastCompactionIndex);
```

This means a loaded session's `History` already contains: `[compaction summary, ...post-compaction messages]`. The data is ready — it just never reaches the agent.

## Session Matching Today

- Client provides session ID to `JoinSession()`. No server-side "find my last session" logic exists.
- Session ID is deterministic: `{channelType}:{conversationId}:{agentId}` — but only when derived by `GatewayHost`, not when client provides one explicitly.
- `ISessionStore` has no `FindActiveSessionAsync()` method — only `ListAsync(agentId)` which returns all sessions.

## Schema Gaps

```sql
-- No session_type column exists. Needed to filter out cron/heartbeat sessions:
ALTER TABLE sessions ADD COLUMN session_type TEXT DEFAULT 'interactive';

-- No resume tracking:
ALTER TABLE sessions ADD COLUMN resume_count INTEGER DEFAULT 0;
```

## Agent Handle Creation

`InProcessIsolationStrategy.CreateAsync()` builds:
- System prompt from workspace files (AGENTS.md, SOUL.md, USER.md, TOOLS.md, etc.)
- Tool registry
- Fresh `Agent` with empty conversation state

No parameter exists to pass prior conversation history into `AgentInitialState`. This is the critical integration point.
