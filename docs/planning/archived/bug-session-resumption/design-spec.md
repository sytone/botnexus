---
id: bug-session-resumption
title: "Session Resumption and Rehydration After Gateway Restart"
type: bug
priority: critical
status: in-progress
created: 2026-04-10
updated: 2026-07-18
author: nova
previous_fix: "Implemented by squad 2026-04-10. Confirmed working briefly but regressed on next gateway restart."
post_ddd_review: "2026-07-14 — Deep review against post-DDD/WebUI codebase. Multiple phases completed or obsoleted by DDD Wave 2 and WebUI rewrite. Phase 1 context bridge remains the critical gap."
tags: [session, gateway, continuity, startup, performance]
ddd_types: [Session, SessionId, AgentId, ChannelKey, SessionStatus, SessionType]
---

# Design Spec: Session Resumption and Rehydration After Gateway Restart

**Type**: Bug Fix + Improvement
**Priority**: Critical (breaks continuity - core value proposition)
**Status**: Reopened (was resolved 2026-04-10, regressed on next cold start)
**Author**: Nova (via Jon)

## Problem

When the BotNexus gateway restarts, agents lose all conversation context. Three gaps combine to break the experience:

1. **No session-to-agent context bridge** - `InProcessIsolationStrategy.CreateAsync()` builds agents with a fresh system prompt and empty conversation. The session's `History` (loaded from SQLite) is never injected into the agent's LLM context.
2. **No startup rehydration** - `GatewayHost.ExecuteAsync()` starts channels and waits. Sessions sit in SQLite but are loaded lazily on first client message. No proactive warming.
3. **No cron session discrimination** - No `session_type` filtering. A naive "most recent active session" query could match a heartbeat session instead of the real conversation.

The result: every gateway restart is a cold start. The agent has no memory of prior conversation, the user has to re-explain context, and it feels broken.

**Target experience**: Gateway restarts are invisible to the user. First message after restart gets a context-aware response with the same quality and latency as any mid-session message.

## Post-DDD Review (2026-07-14)

Deep review performed against the current codebase after DDD refactoring (Wave 2) and WebUI rewrite.
Multiple phases have been completed or made obsolete by architectural changes.

### Phase Status Summary

| Phase | Description                       | Status         | Notes                                                                                  |
|-------|-----------------------------------|----------------|----------------------------------------------------------------------------------------|
| 1     | Session-to-agent context bridge   | **STILL NEEDED** | THE critical remaining bug. `AgentExecutionContext.History` exists but is never wired. |
| 2     | Session matching on reconnect     | **DONE**         | `ResolveOrCreateSessionAsync` + `SessionWarmupService` visibility filtering covers this. |
| 3     | Session discovery API + WebUI     | **OBSOLETE**     | Subscribe-all + `SendMessage(agentId, channelType, content)` model replaces this entirely. |
| 4     | Eager startup rehydration         | **PARTIALLY DONE** | `SessionWarmupService` caches metadata. Gap: no agent handle pre-creation with history. |
| 5     | Cron session tagging              | **DONE**         | `SessionType` smart enum, `session_type` column, inference in `SessionStoreBase`, filtering in warmup. |

### Gap Status Summary

| Gap | Description                                    | Status         | Evidence                                                                                         |
|-----|------------------------------------------------|----------------|--------------------------------------------------------------------------------------------------|
| G1  | No prior history parameter on agent creation   | **HALF-FIXED** | `AgentExecutionContext.History` property exists but `DefaultAgentSupervisor` never populates it and `InProcessIsolationStrategy` never consumes it. |
| G2  | No session-type filtering in session lookup    | **DONE**       | `SessionWarmupService.GetVisibleSessionsAsync()` filters `session.SessionType.Equals(SessionType.UserAgent)`. |
| G3  | No `FindActiveSessionAsync` on ISessionStore   | **COVERED**    | `GatewayHub.ResolveOrCreateSessionAsync()` + warmup service handles session matching. `ListByChannelAsync` also exists. |
| G4  | No session discovery REST endpoint             | **OBSOLETE**   | `SubscribeAll` + `SendMessage` auto-resolve sessions. `GET /api/sessions` exists for inspection. |
| G5  | No startup rehydration service                 | **PARTIALLY COVERED** | `SessionWarmupService` loads metadata on startup. No handle pre-creation or history injection. |
| G6  | No `session_type` column in schema             | **DONE**       | Column in `CREATE TABLE` and migration. `SessionStoreBase.InferSessionType()` + `GatewayHost.ResolveSessionType()`. |
| G7  | WebUI always creates new session on connect    | **DONE**       | `ResolveOrCreateSessionAsync` finds existing sessions by agent+channel via warmup service. |

### Schema Changes Status

| Proposed Column       | Status         | Notes                                                                                   |
|-----------------------|----------------|-----------------------------------------------------------------------------------------|
| `session_type`        | **DONE**       | In `CREATE TABLE` and migration path.                                                   |
| `compaction_summary`  | **NOT NEEDED** | Compaction summaries stored as `SessionEntry.IsCompactionSummary` in `session_history` table. Separate column unnecessary. |
| `last_compaction_at`  | **NOT NEEDED** | Compaction timestamp derivable from `session_history`. Separate column unnecessary.      |
| `resume_count`        | **NOT DONE**   | Low-priority telemetry. Not blocking.                                                   |
| `participants_json`   | **DONE**       | Added in migration. Not in original spec but supports multi-participant sessions.        |

### Key Source File Reference Updates

| Spec Reference                        | Current Location                                                  | Status       |
|---------------------------------------|-------------------------------------------------------------------|--------------|
| `GatewayHost.cs`                      | `src/gateway/BotNexus.Gateway/GatewayHost.cs`                    | ✅ Same role  |
| `InProcessIsolationStrategy.cs`       | `src/gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs` | ✅ Same role  |
| `SqliteSessionStore.cs`               | `src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs`    | ✅ Now extends `SessionStoreBase` |
| `GatewayHub.cs` → `JoinSession()`    | `src/gateway/BotNexus.Gateway.Api/Hubs/GatewayHub.cs`           | ⚠️ `JoinSession` is `[Obsolete]`, replaced by `SubscribeAll` + `SendMessage` |
| `DefaultAgentSupervisor.cs`           | `src/gateway/BotNexus.Gateway/Agents/DefaultAgentSupervisor.cs`  | ✅ Same role, in-memory handle cache |
| `LlmSessionCompactor.cs`             | `src/gateway/BotNexus.Gateway/Sessions/LlmSessionCompactor.cs`  | ✅ Same role  |
| `ISessionStore`                       | `src/gateway/BotNexus.Gateway.Contracts/Sessions/ISessionStore.cs` | ✅ Moved to Contracts |
| `SessionWarmupService` (NEW)          | `src/gateway/BotNexus.Gateway/Sessions/SessionWarmupService.cs`  | New. Handles startup warmup + visibility filtering. |
| `SessionStoreBase` (NEW)              | `src/gateway/BotNexus.Gateway.Sessions/SessionStoreBase.cs`     | New. `InferSessionType()` logic. |
| `SessionType` (NEW)                   | `src/domain/BotNexus.Domain/Primitives/SessionType.cs`          | New. Smart enum: UserAgent, AgentSelf, AgentSubAgent, AgentAgent, Soul, Cron. |
| `AgentExecutionContext`               | `src/domain/BotNexus.Domain/Gateway/Models/AgentExecution.cs`   | ✅ Now has `History` property (unused). |

### Remaining Work Items

1. **Phase 1 — Context Bridge Wiring (CRITICAL)**
   - **`DefaultAgentSupervisor.CreateEntryAsync()`** (line ~190): Change `new AgentExecutionContext { SessionId = sessionId }` to also populate `History` from the session store.
   - **`InProcessIsolationStrategy.CreateAsync()`**: Consume `context.History` — map `SessionEntry` items to LLM conversation messages and inject them into the `Agent`'s initial state.
   - This is the only code change needed to fix the core bug.

2. **Phase 4 — Handle Pre-creation (NICE-TO-HAVE)**
   - `SessionWarmupService` currently caches only `SessionSummary` metadata.
   - For seamless restart, it could also pre-create agent handles (with history) for recent active sessions.
   - This is an optimization, not a bug fix. Phase 1 wiring alone fixes the functional issue.

## Requirements

### Must Have

1. After gateway restart, agent resumes the most recent active session for the channel
2. Conversation context is preserved (via compaction summary + recent history)
3. Agent can reference what was previously discussed without user re-explaining
4. Works for SignalR channel (primary use case)
5. Cron and heartbeat sessions MUST be excluded from session matching (they are ephemeral one-shot sessions)
6. Resumed session notification - the agent MUST be informed this is a resumed session (e.g., system message or metadata flag) so it can behave appropriately

### Should Have

7. Eager rehydration on startup - active sessions are pre-loaded and agent handles pre-created before any client connects
8. Session discovery API - clients can find their active session without knowing the session ID
9. Works for all channel types (cron sessions excluded)
10. Graceful handling when previous session is too old (configurable TTL, default 24h)

### Nice to Have

11. Option to force a new session even when a resumable one exists
12. Session age limit configuration (auto-archive after N hours of inactivity)
13. Multi-client synchronization (same session on multiple devices)

## Architecture

### Key Design Principle: Channel-Scoped Sessions

Sessions are scoped to `agent_id + channel_type`, NOT to individual client connections.

A channel (e.g., SignalR, Telegram, Discord) is a logical communication pipe. Multiple clients can connect to the same channel simultaneously. The session belongs to the channel, not to any individual client.

Think of a session as a chat room - it exists independently of who's connected. Clients come and go. Session matching must NEVER use `client_id` or `connection_id` as part of the match key. The only key is:

```
agent_id + channel_type
```

### Phase 1: Session-to-Agent Context Bridge (the bug fix)

This is the critical missing piece. When creating an agent handle, the session's history must be injected into the LLM context.

**Context injection ordering** (this ordering is critical):

1. **System prompt** - agent identity, rules
2. **Workspace context files** - AGENTS.md, SOUL.md, USER.md, TOOLS.md, etc.
3. **Compaction summary** - if the session was compacted, injected as a system message summarizing prior conversation
4. **Post-compaction messages** - the preserved recent turns
5. **Current user message** - (not applicable during rehydration, only on first real message)

The store's existing load logic already slices from last compaction forward:

```csharp
var lastCompactionIndex = entries.FindLastIndex(e => e.IsCompactionSummary);
if (lastCompactionIndex >= 0)
    entries = entries.GetRange(lastCompactionIndex, entries.Count - lastCompactionIndex);
```

So `session.History` already contains exactly `[compaction_summary?, ...recent_messages]`. The data is ready - it just never reaches the agent.

**Integration point**: `InProcessIsolationStrategy.CreateAsync()` must accept optional prior history:

```csharp
Task<IAgentHandle> CreateAsync(
    AgentInstance agent,
    AgentExecutionContext context,
    IReadOnlyList<SessionEntry>? priorHistory = null,  // NEW
    CancellationToken ct = default);
```

The implementation maps `SessionEntry` to LLM conversation messages and prepends them to the agent's conversation state.

### Phase 2: Session Matching on Reconnect

When a channel connects (e.g., SignalR client connects):

```
1. Look up most recent session WHERE:
   - AgentId = <connecting agent>         -- AgentId value object
   - ChannelKey = <connecting channel>    -- ChannelKey value object (normalized, case-insensitive)
   - Status = SessionStatus.Active        -- SessionStatus smart enum
   - SessionType = SessionType.UserAgent  -- Only user-facing sessions (not Soul, Cron, AgentSelf, etc.)
   - UpdatedAt > (now - session_ttl)
   NOTE: Do NOT include client_id, connection_id, or any client-specific
         identifier in the WHERE clause. Sessions are channel-scoped.

2. If found: RESUME that session (load Session domain model with History)
3. If not found: CREATE new session
```

New method on `ISessionStore`:

```csharp
Task<Session?> FindActiveSessionAsync(
    AgentId agentId, ChannelKey channelKey, CancellationToken ct);
```

Implementation (SQLite):

```sql
SELECT * FROM sessions
WHERE agent_id = @agentId
  AND channel_key = @channelKey
  AND status = @activeStatus              -- SessionStatus.Active
  AND session_type = @userAgentType       -- SessionType.UserAgent
  AND updated_at > @cutoff               -- within TTL
ORDER BY updated_at DESC
LIMIT 1;
```

### Phase 3: Session Discovery API

Clients need a way to find their active session without hardcoding a session ID:

```
GET /api/sessions/active?agentId={agentId}&channelKey={channelKey}
--> { sessionId, agentId, channelKey, messageCount, updatedAt, isRehydrated }
```

Uses `AgentId` and `ChannelKey` value objects for query parameters. Returns `SessionId` value object.

WebUI changes on initial load:
1. Call session discovery API for the default agent + signalr channel
2. If active session found - `JoinSession` with that ID, load history
3. If no active session - create new (existing behavior)

### Phase 4: Eager Startup Rehydration

New hosted service that runs on gateway start, AFTER channels are started:

```
Gateway Start
  |
  +-- Phase 1: Service Initialization (existing)
  |     +-- DI container built
  |     +-- Agent descriptors loaded from disk
  |     +-- Channel adapters started
  |     +-- Cleanup service started
  |
  +-- Phase 2: Session Rehydration (NEW - background)
        +-- Query session store for rehydration candidates
        +-- For each candidate:
        |     +-- Load session into store cache (History, metadata)
        |     +-- Pre-create agent handle with history injected
        |     +-- Mark session as rehydrated
        +-- Log summary: "Rehydrated N sessions for M agents"
```

Rehydration runs in the background - if a client connects before rehydration completes, it falls back to lazy-load (Phase 1+2 fix).

New query method:

```csharp
Task<IReadOnlyList<Session>> FindRehydrationCandidatesAsync(
    TimeSpan maxAge, CancellationToken ct);
```

```sql
SELECT * FROM sessions
WHERE status = @activeStatus
  AND session_type = @userAgentType
  AND updated_at > @cutoff
ORDER BY updated_at DESC;
```

**Bounded execution**:
- Max 3 concurrent handle creations (handle creation involves system prompt, tools, potentially MCP servers)
- Total timeout: 60s - don't block startup forever
- Resilient to individual failures (one bad session doesn't block others)
- Observable via logging

```csharp
public class SessionRehydrationService : IHostedService
{
    // Runs once on startup, after AgentConfigurationHostedService
    // Queries for active UserAgent sessions
    // Pre-loads into store cache
    // Pre-creates agent handles with history
    // Bounded parallelism + timeout
}
```

### Phase 5: Cron Session Tagging

Ensure cron and heartbeat sessions are created with `SessionType.Cron` (or equivalent). This prevents them from interfering with session matching in Phases 2-4.

## Required Schema Changes

```sql
-- Session type discrimination
ALTER TABLE sessions ADD COLUMN session_type TEXT DEFAULT 'UserAgent';

-- Compaction tracking (if not already present)
ALTER TABLE sessions ADD COLUMN compaction_summary TEXT;
ALTER TABLE sessions ADD COLUMN last_compaction_at TEXT;

-- Resume tracking (telemetry only - no behavioral logic)
ALTER TABLE sessions ADD COLUMN resume_count INTEGER DEFAULT 0;
```

## Required Interface Changes

### ISessionStore

```csharp
// Session matching for reconnect (Phase 2)
Task<Session?> FindActiveSessionAsync(
    AgentId agentId, ChannelKey channelKey, CancellationToken ct);

// Rehydration candidates (Phase 4)
Task<IReadOnlyList<Session>> FindRehydrationCandidatesAsync(
    TimeSpan maxAge, CancellationToken ct);
```

### InProcessIsolationStrategy

```csharp
Task<IAgentHandle> CreateAsync(
    AgentInstance agent,
    AgentExecutionContext context,
    IReadOnlyList<SessionEntry>? priorHistory = null,  // NEW
    CancellationToken ct = default);
```

## Configuration

```json
{
  "gateway": {
    "sessions": {
      "resumeEnabled": true,
      "resumeTtlHours": 24,
      "resumeMaxHistoryTokens": 20000,
      "resumeMaxMessages": 20,
      "eagerRehydration": true,
      "rehydrationMaxParallelism": 3,
      "rehydrationTimeoutSec": 60
    }
  }
}
```

## Edge Cases

| Case                                   | Behavior                                                                              |
|----------------------------------------|---------------------------------------------------------------------------------------|
| No active sessions                     | Rehydration completes instantly, no work done                                         |
| Multiple active sessions for same agent| Pick most recently updated. Archive others                                            |
| Cron session is most recent            | Filtered out by `session_type = UserAgent`                                            |
| Session too old (beyond TTL)           | Skipped, start fresh                                                                  |
| Mid-compaction restart                 | Load from last completed compaction; incomplete compaction is lost (re-triggers later) |
| Client connects during rehydration     | Falls back to lazy load (Phases 1+2). Race-safe via thread-safe store cache           |
| Agent descriptor missing for session   | Skip, log warning. Agent may have been removed                                        |
| Rehydration timeout exceeded           | Stop remaining sessions, log which were skipped. They lazy-load on demand             |
| Corrupted session history              | Start fresh with a log warning                                                        |
| Channel switching (user was on SignalR, returns on Telegram) | Different channel = different session. By design                    |

## Multi-Client Synchronization (Future)

When multiple clients connect to the same channel:
1. **Message fan-out** - new messages broadcast to ALL connected clients for that channel
2. **Late-join catch-up** - connecting client loads conversation history
3. **No split-brain** - exactly ONE session per `agent_id + channel_type`

This is analogous to Telegram/Discord - open the app on any device and you see the same chat.

## Implementation Order

| Phase | What                              | Delivers                                    | Depends On | Status         |
|-------|-----------------------------------|---------------------------------------------|------------|----------------|
| 1     | Session-to-agent context bridge   | Agent gets history on resume (the bug fix)  | -          | **STILL NEEDED** — `AgentExecutionContext.History` exists but never wired |
| 2     | Session matching on reconnect     | Gateway finds right session after restart   | Phase 1    | **DONE** — `ResolveOrCreateSessionAsync` + warmup visibility |
| 3     | Session discovery API + WebUI     | Client auto-discovers session, no hardcoded ID | Phase 2 | **OBSOLETE** — Subscribe-all + SendMessage model |
| 4     | Eager startup rehydration         | Sessions pre-warmed before client connects  | Phase 1    | **PARTIALLY DONE** — metadata cached, no handle pre-creation |
| 5     | Cron session tagging              | Cron/heartbeat can't interfere with matching| -          | **DONE** — SessionType smart enum + schema + filtering |

Phases 1+2+5 are the **minimum viable fix**. Phase 3 improves the client experience. Phase 4 makes it feel seamless.

Phase 5 (cron tagging) has no dependencies and can be done in parallel with anything else.

## Testing Plan

1. **Happy path**: Start session, converse, restart gateway, verify agent references prior conversation
2. **With compaction**: Converse until compaction triggers, restart, verify summary loads correctly
3. **Cron exclusion**: Have a cron session more recent than main session. Restart. Verify main session resumes, not cron
4. **Old session**: Set TTL low, wait, verify new session created instead of resuming stale one
5. **Multiple sessions**: Have 2+ sessions for same agent, verify most recent is resumed
6. **Eager rehydration**: Start gateway with 2 active sessions. Verify both rehydrated before first client connects
7. **Cold connect discovery**: After rehydration, connect client without providing session ID. Verify discovery API returns correct session
8. **Context-aware response**: After restart, send "what were we talking about?" - agent should reference prior conversation
9. **Timeout resilience**: Simulate slow handle creation. Verify gateway doesn't hang; remaining sessions lazy-load
10. **Multi-client sync**: Open WebUI in two browsers, verify messages appear on both

## Success Criteria

1. After `botnexus gateway restart`, agent can reference conversation from before the restart
2. No user action required - "it just works"
3. Agent acknowledges resumed context naturally (not "I have no memory of this")
4. First message after restart has the same response quality as mid-session
5. Cron/heartbeat sessions never interfere with session matching
6. Gateway accepts connections immediately (rehydration doesn't block channel startup)

## Repro History

| Date       | Session ID                           | Details                                                                                                  |
|------------|--------------------------------------|----------------------------------------------------------------------------------------------------------|
| 2026-04-10 | edd6b197b2a2...                      | Initial discovery - gateway restart lost all session context. 48-message session, never resumed           |
| 2026-04-10 | (post-fix)                           | Squad implemented fix. Worked briefly, then regressed on next cold start                                 |
| 2026-04-12 | 5b0cea38454f4910a46fedf1ffcc845f     | Gateway restarted, session not rehydrated. Agent woke with blank context. Session existed in SQLite but history never injected into agent LLM context |

## Key Source Files

| File                                | Role                                                                      |
|-------------------------------------|---------------------------------------------------------------------------|
| `GatewayHost.cs`                    | Startup + message processing (no session preload today)                   |
| `InProcessIsolationStrategy.cs`     | Agent handle creation (no history injection today)                        |
| `SqliteSessionStore.cs`             | Persistent store (lazy cache, compaction-aware loading)                   |
| `GatewayHub.cs` -> `JoinSession()`  | Client connection (returns isResumed but doesn't bridge to agent)         |
| `DefaultAgentSupervisor.cs`         | Handle cache (purely in-memory, lost on restart)                          |
| `LlmSessionCompactor.cs`           | Compaction logic (summary stored as SessionEntry with IsCompactionSummary)|
