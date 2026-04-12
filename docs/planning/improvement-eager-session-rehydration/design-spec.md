---
id: improvement-eager-session-rehydration
title: "Eager Session Rehydration on Gateway Start"
type: improvement
priority: high
status: proposed
created: 2026-04-11
author: nova
depends-on: [bug-session-resumption]
tags: [session, gateway, startup, performance, continuity]
---

# Design Spec: Eager Session Rehydration on Gateway Start

**Type**: Improvement
**Priority**: High
**Status**: Proposed
**Author**: Nova (via Jon)
**Depends On**: bug-session-resumption (session-to-agent context bridge must exist)

## Problem

Today when the BotNexus gateway starts, it does **zero session work**. Sessions sit in SQLite, but:

1. They're loaded **lazily** — only on first client message or `JoinSession` call
2. Even when loaded, session history is **never injected into the agent's LLM context**
3. The agent handle is created fresh with an empty conversation

This means every gateway restart produces a cold start: the agent has no memory of prior conversation, the first message has extra latency (session load + handle creation), and the user experience feels broken.

## Goal

When the gateway starts, it should **eagerly rehydrate all active sessions** so that when a client connects or sends a message, the session and agent are already warm and ready. The client UI just needs to catch up on conversation history it doesn't have locally.

**Target experience**: Gateway restarts are invisible to the user. First message after restart has the same response quality and latency as any mid-session message.

## Architecture

### Two-Phase Startup

```
Gateway Start
  │
  ├─ Phase 1: Service Initialization (existing)
  │   ├─ DI container built
  │   ├─ Agent descriptors loaded from disk
  │   ├─ Channel adapters started
  │   └─ Cleanup service started
  │
  └─ Phase 2: Session Rehydration (NEW)
      ├─ Query session store for all rehydration candidates
      ├─ For each candidate:
      │   ├─ Load session into store cache (History, metadata)
      │   ├─ Pre-create agent handle with history injected
      │   └─ Mark session as rehydrated in metadata
      └─ Log summary: "Rehydrated N sessions for M agents"
```

Phase 2 runs **after** channels are started so the gateway can accept connections immediately. Rehydration happens in the background — if a client connects before rehydration completes, it falls back to the existing lazy-load path.

### Rehydration Candidate Query

New method on `ISessionStore`:

```csharp
Task<IReadOnlyList<GatewaySession>> FindRehydrationCandidatesAsync(
    TimeSpan maxAge, CancellationToken ct);
```

SQL (for SQLite):
```sql
SELECT * FROM sessions
WHERE status = 'Active'
  AND session_type != 'cron'          -- exclude ephemeral sessions
  AND updated_at > @cutoff            -- within TTL
ORDER BY updated_at DESC;
```

This requires the `session_type` column from the session-resumption spec. Until that's added, a reasonable workaround is filtering by session ID pattern or metadata.

### Session-to-Agent Context Bridge

This is the **critical missing piece** (shared with bug-session-resumption). When creating an agent handle, the session's history must be injected into the LLM context.

**Context injection ordering** (matches existing session-resumption spec):

1. **System prompt** — agent identity, rules, workspace files
2. **Compaction summary** — if the session was compacted, this is the "story so far"
3. **Post-compaction messages** — the preserved recent turns
4. **Current user message** — (not applicable during rehydration, only on first real message)

The store's existing load logic already slices from last compaction forward, so `session.History` contains exactly `[compaction_summary?, ...recent_messages]`. This needs to be passed through to `InProcessIsolationStrategy.CreateAsync()`.

**Integration point**: `AgentInitialState` (or a new parameter on agent creation) needs to accept prior conversation history. The isolation strategy must map `SessionEntry` list → LLM conversation messages.

### Agent Handle Pre-Creation

During rehydration, the supervisor pre-creates handles:

```csharp
// In SessionRehydrationService (new hosted service)
foreach (var session in candidates)
{
    var handle = await _supervisor.GetOrCreateAsync(
        session.AgentId, session.SessionId, ct,
        priorHistory: session.History);  // NEW parameter

    session.Metadata["rehydrated"] = true;
    session.Metadata["rehydratedAt"] = DateTimeOffset.UtcNow;
    await _sessions.SaveAsync(session, ct);
}
```

**Important**: Handle creation involves building the system prompt, loading tools, and potentially starting MCP servers. This has real cost. The rehydration service should:

- Run with bounded parallelism (e.g., max 3 concurrent handle creations)
- Have a total timeout (e.g., 60s) — don't block startup forever
- Log progress so it's observable
- Be resilient to individual failures (one bad session shouldn't block others)

### Client Connection (Post-Rehydration)

When a client connects after rehydration:

1. `JoinSession(agentId, sessionId)` — session is already in cache, instant return
2. Client receives `isResumed = true` + `messageCount`
3. Client loads conversation history via existing `GetSessionHistory` API
4. Client renders history in UI — user sees their prior conversation
5. First `SendMessage` — handle already exists with context loaded, responds immediately

If the client doesn't know the session ID (e.g., new browser tab), the gateway needs a **session discovery** endpoint:

```
GET /api/sessions/active?agentId=nova&channelType=signalr
→ Returns the most recent active session for that agent+channel pair
```

The WebUI would call this on connect to find the right session to join, rather than always creating a new one.

## Required Changes

### Schema

```sql
-- Session type discrimination (may already be in session-resumption spec)
ALTER TABLE sessions ADD COLUMN session_type TEXT DEFAULT 'interactive';

-- Rehydration tracking (optional, diagnostics)
ALTER TABLE sessions ADD COLUMN resume_count INTEGER DEFAULT 0;
```

### ISessionStore

```csharp
// New method for rehydration candidates
Task<IReadOnlyList<GatewaySession>> FindRehydrationCandidatesAsync(
    TimeSpan maxAge, CancellationToken ct);

// New method for session discovery by channel
Task<GatewaySession?> FindActiveSessionAsync(
    string agentId, string channelType, CancellationToken ct);
```

### Agent Handle Creation

`InProcessIsolationStrategy.CreateAsync()` must accept optional prior history:

```csharp
Task<IAgentHandle> CreateAsync(
    AgentInstance agent,
    AgentExecutionContext context,
    IReadOnlyList<SessionEntry>? priorHistory = null,  // NEW
    CancellationToken ct = default);
```

The implementation maps `SessionEntry` → LLM messages and prepends them to the agent's conversation state.

### New Hosted Service: SessionRehydrationService

```csharp
public class SessionRehydrationService : IHostedService
{
    // Runs once on startup
    // Queries for active sessions
    // Pre-loads into store cache
    // Pre-creates agent handles with history
    // Bounded parallelism + timeout
    // Logs summary
}
```

Registration order matters — this should run **after** `AgentConfigurationHostedService` (needs agent descriptors) and **after** channel adapters are started (so connections can be accepted during rehydration).

### Session Discovery API

```
GET /api/sessions/active?agentId={agentId}&channelType={channelType}
→ { sessionId, agentId, channelType, messageCount, updatedAt, isRehydrated }
```

The WebUI (and other clients) should call this on connect to find an existing session rather than blindly creating a new one.

### WebUI Changes

On initial load:
1. Call session discovery API for the default agent + signalr channel
2. If active session found → `JoinSession` with that ID, load history
3. If no active session → create new (existing behavior)

## Configuration

```json
{
  "gateway": {
    "sessions": {
      "eagerRehydration": true,
      "rehydrationMaxAgeSec": 86400,
      "rehydrationMaxParallelism": 3,
      "rehydrationTimeoutSec": 60,
      "resumeEnabled": true,
      "resumeTtlHours": 24
    }
  }
}
```

## Edge Cases

| Case | Behavior |
|------|----------|
| No active sessions | Rehydration completes instantly, no work done |
| 50+ active sessions | Bounded parallelism prevents overload; oldest beyond TTL skipped |
| Cron session is most recent | Filtered out by `session_type != 'cron'` |
| Session was mid-compaction when gateway died | Store loads from last completed compaction; incomplete compaction is lost (acceptable — user message triggers re-compaction) |
| Client connects during rehydration | Falls back to lazy load (existing path). Race-safe because store cache is thread-safe |
| Agent descriptor missing for a session | Skip that session, log warning. Agent may have been removed |
| Multiple active sessions per agent+channel | Pick most recent `updated_at`. Archive others |
| Rehydration timeout exceeded | Stop rehydrating remaining sessions, log which were skipped. They'll lazy-load on demand |

## Relationship to Existing Specs

- **bug-session-resumption**: This spec builds on top of it. The context bridge (Gap 1 in that spec) is a prerequisite. Eager rehydration is the "go further" step — don't just bridge on first message, bridge proactively on startup.
- **bug-session-switching-ui**: WebUI session switching becomes simpler when sessions are pre-loaded and discoverable via API.
- **feature-subagent-ui-visibility**: Sub-agent sessions would NOT be rehydration candidates (they're ephemeral).

## Success Criteria

1. After `botnexus gateway restart`, active sessions are loaded into memory within 60s
2. First message after restart gets context-aware response (agent references prior conversation)
3. No user action required — session discovery + join happens automatically
4. WebUI shows prior conversation history on reconnect
5. Cron/heartbeat sessions are never rehydrated as main sessions
6. Gateway accepts connections immediately (rehydration doesn't block channel startup)

## Implementation Order

1. **Schema migration**: Add `session_type` column
2. **Session-to-agent context bridge**: Modify `InProcessIsolationStrategy` to accept + inject prior history (this is the bug-session-resumption fix)
3. **Session discovery API**: `FindActiveSessionAsync` + REST endpoint
4. **WebUI session discovery**: Call discovery API on connect
5. **SessionRehydrationService**: Eager startup rehydration
6. **Cron session tagging**: Ensure cron/heartbeat sessions are created with `session_type = 'cron'`

Steps 1-4 deliver the "session resume works" fix. Step 5 adds the "it's already warm" improvement. Step 6 prevents cron interference.

## Testing Plan

1. **Warm start**: Start gateway with 2 active sessions in DB. Verify both are rehydrated before first client connects.
2. **Cold connect**: After rehydration, connect client without providing session ID. Verify discovery API returns correct session and UI shows history.
3. **First message context**: After restart, send "what were we talking about?" — agent should reference prior conversation.
4. **Cron exclusion**: Have a cron session that's more recent than the main session. Verify main session is rehydrated, not cron.
5. **Timeout resilience**: Simulate slow handle creation. Verify gateway doesn't hang; remaining sessions lazy-load.
6. **Concurrent connect**: Connect client while rehydration is in progress. Verify no errors, session loads correctly.
7. **No active sessions**: Start gateway with only expired/closed sessions. Verify no rehydration work done, clean startup.
