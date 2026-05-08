---
id: bug-sqlite-session-lock
title: "SQLite Session Store Global Lock Blocks Multi-Agent Concurrency"
type: bug
priority: critical
status: draft
created: 2026-04-16
tags: [concurrency, sqlite, sessions, performance, multi-agent]
---

# Bug: SQLite Session Store Global Lock Blocks Multi-Agent Concurrency

**Status:** draft
**Priority:** critical
**Created:** 2026-04-16

## Problem

When one agent is processing a message (especially during `SaveAsync`), all other agents stop responding. The root cause is a global `SemaphoreSlim(1, 1)` in `SqliteSessionStore` that serializes ALL session operations across ALL agents and sessions.

## Root Cause

`src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs` line 23:

```csharp
private readonly SemaphoreSlim _lock = new(1, 1);  // ONE lock for ALL sessions
```

Every operation (`GetAsync`, `SaveAsync`, `GetOrCreateAsync`, `EnumerateSessionsAsync`) acquires this lock and holds it for the entire I/O duration. When Agent A saves a session with large history (potentially seconds), every other agent attempting any session operation is blocked.

### Blocking Chain

```
Agent A: SaveAsync → acquires _lock → SQLite INSERT/UPDATE (seconds for large histories)
Agent B: GetAsync  → waits on _lock → BLOCKED until Agent A finishes
Agent C: SaveAsync → waits on _lock → BLOCKED behind Agent B
```

## Impact

- Multi-agent system degrades to single-agent serialization under load
- User sees agents "freeze" while another agent is processing
- Particularly severe with large session histories (compaction, tool-heavy conversations)

## Requirements

### Must Have
- Different sessions can be read/written concurrently
- One agent's save operation does not block another agent's read/write
- SQLite WAL mode enabled for concurrent readers

### Should Have
- Per-session lock granularity (not global)
- Connection pooling for concurrent operations

### Nice to Have
- Read operations don't require locks (WAL mode allows concurrent reads)
- Metrics for lock contention

## Proposed Fix

1. **Enable WAL mode** on SQLite database — allows concurrent readers with a single writer
2. **Replace global lock with per-session locks** — `ConcurrentDictionary<string, SemaphoreSlim>` keyed by session ID
3. **Separate read and write paths** — reads don't need locks under WAL mode, only writes need per-session serialization
4. **Connection pooling** — use `SqliteConnection` pool instead of creating per-operation

## Files to Change

- `src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs` — primary fix
- Tests in `tests/gateway/BotNexus.Gateway.Tests/Sessions/` — concurrency test coverage
