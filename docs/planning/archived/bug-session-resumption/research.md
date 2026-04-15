# Research: Session Resumption and Rehydration

## Post-DDD Review (2026-07-14)

> **Deep review performed against post-DDD (Wave 2) and WebUI rewrite codebase.**
> Many gaps identified in the original research are now closed. The critical remaining
> gap is the Phase 1 context bridge wiring — `AgentExecutionContext.History` exists as
> a property but is never populated by the supervisor or consumed by the isolation strategy.

## Problem Statement

When the BotNexus gateway restarts, agents lose all conversation context. Sessions persist in SQLite but the history is never injected back into the agent's LLM context. This has been observed multiple times (2026-04-10, 2026-04-12) and a previous fix regressed.

## Current State

### What Works

- Sessions persist in SQLite across gateway restarts
- Compaction summaries are stored as `SessionEntry` with `IsCompactionSummary=true`
- Session load logic already slices from last compaction forward - `session.History` contains `[compaction_summary?, ...recent_messages]`
- `JoinSession()` returns `isResumed = true` + `messageCount` for existing sessions
- Session history API returns all messages for a session

### What's Broken (Updated 2026-07-14)

~~Three~~ **One** gap remains that breaks the experience:

1. **No session-to-agent context bridge** — `AgentExecutionContext` now has a `History` property, but `DefaultAgentSupervisor.CreateEntryAsync()` creates context as `new AgentExecutionContext { SessionId = sessionId }` (History defaults to empty). `InProcessIsolationStrategy.CreateAsync()` only uses `context.SessionId`, never `context.History`. Session `History` is never injected into the agent.
2. ~~**No startup rehydration**~~ — **PARTIALLY ADDRESSED**: `SessionWarmupService` (new `IHostedService`) pre-loads session metadata on startup and caches `SessionSummary` objects. It doesn't pre-create agent handles, but the functional gap is in point 1.
3. ~~**No cron session discrimination**~~ — **DONE**: `SessionType` smart enum exists (`BotNexus.Domain.Primitives.SessionType`) with values: UserAgent, AgentSelf, AgentSubAgent, AgentAgent, Soul, Cron. `session_type` column in SQLite schema. `SessionWarmupService.GetVisibleSessionsAsync()` filters by `SessionType.UserAgent`.

### Cold Start vs Reconnect - Why the Fix Regressed

The squad's 2026-04-10 fix likely covered SignalR reconnect (client reconnects to a still-running gateway) but NOT gateway cold start (process exit + relaunch):

| Scenario           | Gateway process | In-memory state | Session lookup required? |
|--------------------|-----------------|-----------------|--------------------------|
| SignalR reconnect  | Still running   | Preserved       | No (already in memory)   |
| Gateway cold start | New process     | Empty           | Yes (must query store)   |

This explains why the fix worked initially (reconnect scenario) but failed on the next restart (cold start scenario).

## Source Code Analysis

### Key Source Files (Updated 2026-07-14)

| File                                | Role                                                               | Status |
|-------------------------------------|--------------------------------------------------------------------|--------|
| `GatewayHost.cs`                    | Startup + message processing. No session preload                   | ✅ Same location |
| `InProcessIsolationStrategy.cs`     | Agent handle creation. Has `context.History` available but ignores it | ✅ Moved to `Isolation/` |
| `SqliteSessionStore.cs`             | Persistent store. Lazy cache, compaction-aware loading. Now extends `SessionStoreBase` | ✅ Updated |
| `GatewayHub.cs` → `JoinSession()`  | Client connection. `[Obsolete]` — replaced by `SubscribeAll` + `SendMessage` | ⚠️ Deprecated |
| `DefaultAgentSupervisor.cs`         | Handle cache. Creates `AgentExecutionContext` without History       | ✅ Moved to `Agents/` |
| `LlmSessionCompactor.cs`           | Compaction. Summary stored as SessionEntry.IsCompactionSummary     | ✅ Same location |
| `SessionWarmupService.cs` (NEW)     | Startup warmup + visibility filtering for UserAgent sessions       | 🆕 New |
| `SessionStoreBase.cs` (NEW)         | Base class with `InferSessionType()` logic                         | 🆕 New |
| `SessionType.cs` (NEW)              | Domain smart enum: UserAgent, Cron, Soul, AgentSelf, etc.          | 🆕 New |
| `AgentExecutionContext` (UPDATED)   | Now has `History` property — ready for wiring                      | 🆕 Updated |

### Project Structure (Updated 2026-07-14)

| Project                              | Role                                                                       |
|--------------------------------------|----------------------------------------------------------------------------|
| `BotNexus.Domain`                    | Domain primitives: SessionType, SessionId, AgentId, ChannelKey, Session    |
| `BotNexus.Gateway.Contracts`         | Interfaces: ISessionStore, IIsolationStrategy, ISessionWarmupService       |
| `BotNexus.Gateway.Abstractions`      | Type forwards to Domain models (GatewaySession, SessionEntry, AgentExecutionContext) |
| `BotNexus.Gateway.Sessions`          | Store implementations: Sqlite, File, InMemory + SessionStoreBase           |
| `BotNexus.Gateway`                   | Core: GatewayHost, Compactor, Supervisor, Isolation, SessionWarmupService  |
| `BotNexus.Gateway.Api`               | ASP.NET host: GatewayHub, SessionsController                              |

### Session Load Behavior (SQLite)

On load, the store slices from last compaction forward:

```csharp
var lastCompactionIndex = entries.FindLastIndex(e => e.IsCompactionSummary);
if (lastCompactionIndex >= 0)
    entries = entries.GetRange(lastCompactionIndex, entries.Count - lastCompactionIndex);
```

This means `session.History` already contains the right data. The problem is delivery, not storage.

### Agent Handle Creation (Updated 2026-07-14)

`InProcessIsolationStrategy.CreateAsync()` builds:
- System prompt from workspace files (AGENTS.md, SOUL.md, USER.md, TOOLS.md)
- Tool registry
- Fresh `Agent` with empty conversation state

`AgentExecutionContext` now has a `History` property (`IReadOnlyList<SessionEntry>`, defaults to `[]`). However:
- **`DefaultAgentSupervisor.CreateEntryAsync()`** creates context as `new AgentExecutionContext { SessionId = sessionId }` — History is never populated.
- **`InProcessIsolationStrategy.CreateAsync()`** only reads `context.SessionId` — `context.History` is never consumed.

The interface is ready. The wiring is the only remaining work for Phase 1.

### Session Matching (Updated 2026-07-14)

- ~~Client provides session ID to `JoinSession()`. No server-side "find my last session" logic exists.~~ **DONE**: `JoinSession` is `[Obsolete]`. New `SendMessage(agentId, channelType, content)` auto-resolves sessions via `ResolveOrCreateSessionAsync`.
- `ResolveOrCreateSessionAsync` queries `SessionWarmupService.GetAvailableSessionsAsync(agentId)`, filters by matching channel, picks most recent.
- `SessionWarmupService` filters by `SessionType.UserAgent` and `Status ∈ {Active, Suspended, Sealed}` and `UpdatedAt >= retention cutoff`.
- `ISessionStore` has `ListByChannelAsync(AgentId, ChannelKey)` — returns sessions for agent+channel, ordered by created time descending.
- No explicit `FindActiveSessionAsync` method exists, but `ResolveOrCreateSessionAsync` + warmup covers the same functionality.

### Schema (Updated 2026-07-14)

Current schema **already has** `session_type` column (in `CREATE TABLE` and migration path). Also has `participants_json`.

```sql
-- sessions table includes:
--   session_type TEXT           ✅ DONE
--   participants_json TEXT      ✅ DONE (not in original spec)

-- session_history table includes:
--   is_compaction_summary INTEGER NOT NULL DEFAULT 0   ✅ DONE (compaction stored as history entry, not separate column)
```

~~Needed additions~~ Only remaining (low-priority):
```sql
ALTER TABLE sessions ADD COLUMN resume_count INTEGER DEFAULT 0;  -- telemetry only
```

Note: `compaction_summary` and `last_compaction_at` columns are NOT needed — compaction data is stored as `SessionEntry` records with `IsCompactionSummary=true` in the `session_history` table.

## Industry Research

### Claude Code
- Uses checkpoints and session files for resumption
- `/resume` command to continue a previous session
- Compaction summary persisted and re-injected

### OpenClaw (predecessor system)
- Session JSONL files persist full history
- Compaction summary written as a session entry type
- `readPostCompactionContext()` function handles context refresh post-compaction
- Post-compaction injects AGENTS.md "Session Startup" and "Red Lines" sections

### General Patterns
- Most persistent chat systems treat sessions as chat rooms (channel-scoped, not client-scoped)
- Session discovery APIs (find my active session) are standard in multi-device messaging
- Eager warming on startup is common in production systems to avoid cold-start latency

## Key Findings

1. **The data is ready** - SQLite has everything needed. The gap is purely in the context bridge (getting history into the agent's LLM conversation).
2. **Cold start vs reconnect are different code paths** - the regression likely means only reconnect was fixed.
3. **Session matching needs server-side logic** - clients shouldn't need to know their session ID; the gateway should resolve it from `agent_id + channel_key`.
4. **Cron session interference is real** - without type discrimination, heartbeat sessions can mask the real conversation session.
5. **The fix is phased** - context bridge is Phase 1 (the bug), eager rehydration is Phase 4 (the improvement). Both use the same underlying mechanism.

## Gaps Identified (Updated 2026-07-14)

| Gap | Description                                    | Phase | Status         | Evidence                                                    |
|-----|------------------------------------------------|-------|----------------|-------------------------------------------------------------|
| G1  | No prior history parameter on agent creation   | 1     | **HALF-FIXED** | `AgentExecutionContext.History` exists but never wired      |
| G2  | No session-type filtering in session lookup    | 2, 5  | **DONE**       | `SessionWarmupService.GetVisibleSessionsAsync()` filters UserAgent |
| G3  | No `FindActiveSessionAsync` on ISessionStore   | 2     | **COVERED**    | `ResolveOrCreateSessionAsync` + warmup + `ListByChannelAsync` |
| G4  | No session discovery REST endpoint             | 3     | **OBSOLETE**   | `SubscribeAll` + `SendMessage` model replaces this          |
| G5  | No startup rehydration service                 | 4     | **PARTIALLY COVERED** | `SessionWarmupService` loads metadata, not handles   |
| G6  | No `session_type` column in schema             | 5     | **DONE**       | Column in CREATE TABLE + migration                          |
| G7  | WebUI always creates new session on connect    | 3     | **DONE**       | `ResolveOrCreateSessionAsync` finds existing by agent+channel |

## Questions for the Squad

1. What exactly was fixed on 2026-04-10? Was it the SignalR reconnect handler, the session startup path, or both?
2. Does the current `JoinSession` code path query the session store on cold start, or does it rely on in-memory state?
3. Is there already a mechanism to distinguish cron sessions from interactive ones? (A column, a naming convention, anything?)
4. What's the preferred approach for the context bridge - new parameter on `CreateAsync`, or a separate `InjectHistory` method on the handle?
5. Are there any concerns with the bounded-parallelism approach for eager rehydration (3 concurrent, 60s timeout)?
