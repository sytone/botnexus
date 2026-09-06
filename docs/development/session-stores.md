# Session Stores and Persistence

This document describes BotNexus's session storage architecture, including persistence strategies, query patterns, and lifecycle management.

## Overview

Sessions are persisted via `ISessionStore`, which supports two backend implementations:

- **InMemorySessionStore**: Non-durable, for testing and development
- **SqliteSessionStore**: SQLite database, production default

All implementations share common query patterns and lifecycle operations.

## ISessionStore Interface

```csharp
public interface ISessionStore
{
    // Core CRUD
    Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken ct = default);
    Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken ct = default);
    Task SaveAsync(GatewaySession session, CancellationToken ct = default);
    Task<SessionSaveOutcome> SaveAsync(GatewaySession session, SessionWriteFence fence, CancellationToken ct = default);
    Task DeleteAsync(SessionId sessionId, CancellationToken ct = default);

    // Atomic mutations (read-modify-write under the store's own lock)
    Task<SessionAppendMutationResult> AppendEntriesAsync(...);
    Task<SessionMetadataMutationResult> PatchMetadataAsync(...);
    Task<SessionStatusMutationResult> TransitionStatusAsync(...);

    // Queries
    Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken ct = default);
    Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(...);
    Task<SessionSummaryPage> ListSummaryPageAsync(...);
    Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(...);
    Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(...);
    Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(...);
    Task<SessionStats?> GetStatsAsync(AgentId? agentId = null, CancellationToken ct = default);

    // Lifecycle
    Task ArchiveAsync(SessionId sessionId, CancellationToken ct = default);

    // Sub-agent session records
    Task SaveSubAgentSessionAsync(SubAgentInfo info, CancellationToken ct = default);
    Task UpdateSubAgentSessionAsync(...);
    Task<IReadOnlyList<SubAgentSessionSummary>> ListSubAgentSessionsAsync(...);
    Task<IReadOnlyList<SubAgentSessionSummary>> ListAllSubAgentSessionsAsync(...);
}
```

See [ISessionStore.cs](../../src/gateway/BotNexus.Gateway.Contracts/Sessions/ISessionStore.cs)
for the authoritative signatures and per-member contracts.

### The fenced save overload

`SaveAsync(session, fence, ct)` is the write path the gateway's post-run finalizer uses. It
persists **only if** the on-disk row still matches the run identity captured in the
`SessionWriteFence` at run start. When the row was deleted, sealed by a competing reset, or
rebound to a different conversation while the run was in flight, the write is skipped and
`SessionSaveOutcome.Rebound` is returned rather than resurrecting or clobbering the row
(issue #1518). The unfenced `SaveAsync` keeps unconditional create-or-update semantics for
pre-run write-ahead saves that must be able to create the row.

The default implementation re-reads via `GetAsync` and applies the fence before delegating.
Stores that can do the re-read and the write under a single lock - the SQLite store - override
it to close the check-then-write window atomically.

## GatewaySession Model

```csharp
public class GatewaySession
{
    public SessionId SessionId { get; set; }
    public AgentId AgentId { get; set; }
    public SessionType SessionType { get; set; }
    public SessionStatus Status { get; set; }
    
    public ChannelKey? ChannelType { get; set; }
    public string? CallerId { get; set; }
    
    public List<SessionParticipant> Participants { get; set; } = [];
    public List<SessionEntry> History { get; set; } = [];
    public Dictionary<string, object?> Metadata { get; set; } = [];
    
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
```

**SessionEntry:**

```csharp
public record SessionEntry
{
    public MessageRole Role { get; init; }       // User, Assistant, System
    public string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}
```

**SessionParticipant:**

```csharp
public record SessionParticipant
{
    public ParticipantType Type { get; init; }  // User, Agent, System
    public string Id { get; init; }             // connectionId or agentId
    public string? Role { get; init; }          // "initiator", "target", etc.
}
```

## SessionStoreBase

Abstract base class implementing the query, mutation, and lifecycle logic that is identical
across every backing store, so a concrete store only implements storage access. Its abstract
members are `GetAsync`, `GetOrCreateAsync`, `SaveAsync`, `DeleteAsync`, `ArchiveAsync`, and the
protected `EnumerateSessionsAsync`. Everything else - the fenced `SaveAsync` overload, the three
atomic mutations (`AppendEntriesAsync`, `PatchMetadataAsync`, `TransitionStatusAsync`), the
summary/paging/channel/conversation queries, and the sub-agent session records - is `virtual`,
so a store overrides only what it can do better (the SQLite store overrides the fenced save and
the mutations to close the check-then-write window under one lock).

It also owns the **archive drain**: `ConfigureArchiveDrain(runDrain, drainTimeout)` lets the
gateway supply a drain so `ArchiveAsync` waits for an in-flight run to finish before sealing,
bounded by `DefaultArchiveDrainTimeout` (30 seconds).

See [SessionStoreBase.cs](../../src/gateway/BotNexus.Gateway.Sessions/SessionStoreBase.cs)

## InMemorySessionStore

**Characteristics:**

- Non-durable (lost on restart)
- Fast (in-process dictionary)
- Suitable for testing and development
- No concurrency safety across processes

Uses `ConcurrentDictionary<string, GatewaySession>` for O(1) lookups. Non-durable — all data lost on restart. Suitable for testing and development.

See [InMemorySessionStore.cs](../../src/gateway/BotNexus.Gateway.Sessions/InMemorySessionStore.cs)

## FileSessionStore

The file-backed implementation stores history as JSONL plus a small metadata sidecar. Ordinary saves append only the unpersisted history tail to the JSONL file, so adding one turn does not serialize and recreate the existing transcript. Explicit destructive history mutations, such as compaction projection or sentinel removal, rewrite the JSONL file from the reconciled in-memory history because a line-oriented file has no stable row-update primitive. Whole-session deletion and archival remain explicit lifecycle operations.

See [FileSessionStore.cs](../../src/gateway/BotNexus.Gateway.Sessions/FileSessionStore.cs)

## SqliteSessionStore

**Characteristics:**

- Durable and performant
- Indexed queries (fast filtering)
- Transactional updates
- Production-ready
- Single-file database

**Schema:**

```sql
CREATE TABLE IF NOT EXISTS sessions (
    id TEXT PRIMARY KEY,
    agent_id TEXT NOT NULL,
    channel_type TEXT,
    caller_id TEXT,
    session_type TEXT NOT NULL DEFAULT 'user-agent',
    participants_json TEXT,
    status TEXT NOT NULL DEFAULT 'active',
    metadata TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    conversation_id TEXT
);

CREATE TABLE IF NOT EXISTS session_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT,
    role TEXT,
    content TEXT,
    timestamp TEXT,
    tool_name TEXT,
    tool_call_id TEXT,
    is_compaction_summary INTEGER NOT NULL DEFAULT 0,
    is_crash_sentinel INTEGER NOT NULL DEFAULT 0,
    is_history INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_sessions_agent_id ON sessions(agent_id);
CREATE INDEX idx_sessions_status ON sessions(status);
CREATE INDEX idx_sessions_session_type ON sessions(session_type);
CREATE INDEX idx_sessions_created_at ON sessions(created_at);
```

**Key behaviors:** Parameterized queries throughout, conflict-targeted upserts for the session row, JSON serialization for complex fields (`participants_json`, `metadata`), lazy schema initialization via `EnsureCreatedAsync`, and ISO 8601 date formatting. Session history lives in the separate `session_history` table and is append-oriented: an ordinary aggregate save inserts only the unpersisted tail. Explicit destructive mutations such as compaction projection or crash-sentinel cleanup reconcile rows by their durable `session_history.id`, preserving unchanged row identities and deleting only rows that were explicitly removed. Whole-session history deletion is reserved for deleting the session itself or repairing a dangling session whose owning conversation no longer exists. The existing `session.save` activity reports bounded `append`/`reconcile` mode plus numeric inserted, updated, and deleted row counts; no session or row identifier is added as a mutation tag.

### History entry flags

The `session_history` table stores the full transcript — *nothing is ever deleted by compaction*. Three boolean flags determine how the runtime interprets each entry:

| Flag | Set when | Effect |
| --- | --- | --- |
| `is_compaction_summary` | Compactor inserts the synthetic summary entry that folds older turns | Sent to the LLM as a `system` message so the model still has the compressed context |
| `is_crash_sentinel` | Gateway is mid-turn at shutdown and writes a placeholder so the next start can recover | Excluded from the LLM context projection and from any future summarisation prompt |
| `is_history` | Compactor marks summarised entries — they remain in the store for the transcript but are hidden from the LLM | Excluded from the LLM context projection; not eligible for re-summarisation on later compaction cycles |

On load, `SessionCompaction.ApplyLegacyHistoryProjection` collapses pre-Phase-3a databases forward — any session that has multiple `is_compaction_summary=true` rows with `is_history=false` (the old code applied a load-time slice to hide older summaries) gets all-but-latest summary flipped to `is_history=true` in memory, and the new state persists on the next save. The migration is idempotent.

See [SqliteSessionStore.cs](../../src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs)

## Existence Queries

**ExistenceQuery:**

```csharp
public record ExistenceQuery
{
    public AgentId? AgentId { get; init; }
    public SessionType? SessionType { get; init; }
    public SessionStatus? Status { get; init; }
    public ChannelKey? ChannelType { get; init; }
    public DateTimeOffset? CreatedAfter { get; init; }
    public DateTimeOffset? CreatedBefore { get; init; }
    public int? Limit { get; init; }
}
```

**Query Implementation:**

Builds dynamic SQL from `ExistenceQuery` filters — each non-null property adds a parameterized `WHERE` clause. Results ordered by `updated_at DESC` with optional `LIMIT`.

See [SqliteSessionStore.cs](../../src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs)

**Example Queries:**

```csharp
var agentId = AgentId.From("my-agent");

// Find all active UserAgent sessions the agent owns or participates in
var activeQuery = new ExistenceQuery
{
    SessionType = SessionType.UserAgent,
    Status = SessionStatus.Active
};
var sessions = await _sessionStore.GetExistenceAsync(agentId, activeQuery, ct);

// Find that agent's soul sessions created in the last 7 days
var soulQuery = new ExistenceQuery
{
    SessionType = SessionType.Soul,
    CreatedAfter = DateTimeOffset.UtcNow.AddDays(-7),
    Limit = 100
};
var recentSoulSessions = await _sessionStore.GetExistenceAsync(agentId, soulQuery, ct);
```

## Session Cleanup

**Automatic Cleanup:**

There is no store-level bulk-expiry method. Cleanup is driven entirely by
`SessionCleanupService`, which enumerates sessions and calls `DeleteAsync` per session for the
rows that qualify.

**SessionCleanupService:**

A `BackgroundService` that wakes on `CheckInterval` and calls `RunCleanupOnceAsync`. Each pass
**skips any session with an agent run in flight** (logged, retried next pass) and then deletes:

- sessions idle beyond `SessionTtl`;
- closed sessions older than `ClosedSessionRetention`, when configured;
- cron sessions whose run did no work, older than `CronNoopRetention`.

Each deletion emits a `SessionLifecycleEventType.Deleted` event. A failed iteration is logged as
a warning and retried on the next interval rather than terminating the service.

See [SessionCleanupService.cs](../../src/gateway/BotNexus.Gateway/SessionCleanupService.cs)

**SessionCleanupOptions:**

```csharp
public sealed class SessionCleanupOptions
{
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan? ClosedSessionRetention { get; set; }
    public TimeSpan? CronNoopRetention { get; set; } = TimeSpan.FromDays(7);
}
```

## Session Warmup

**ISessionWarmupService:**

Prepares sessions for client consumption (filtering, sorting, hydration):

```csharp
public interface ISessionWarmupService
{
    Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(
        CancellationToken ct = default);
}
```

**SessionSummary:**

```csharp
public record SessionSummary
{
    public string SessionId { get; init; }
    public string AgentId { get; init; }
    public string SessionType { get; init; }
    public string Status { get; init; }
    public string? ChannelType { get; init; }
    public int MessageCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
```

Queries all registered agents, filters to visible sessions (Active/Suspended, UserAgent/Soul, non-expired), and projects to lightweight `SessionSummary` records sorted by `UpdatedAt` descending.

See [SessionWarmupService.cs](../../src/gateway/BotNexus.Gateway/Sessions/SessionWarmupService.cs)

## Summary

**Storage Strategies:**

| Store | Durability | Performance | Concurrency | Use Case |
|-------|-----------|-------------|-------------|----------|
| InMemory | None | Fastest | Single process | Testing, dev |
| SQLite | Durable | Fast | Multi-process | Production |

**Key Architectural Decisions:**

1. **Abstract interface**: Pluggable storage backends
2. **SessionStoreBase**: Common logic in base class
3. **GetOrCreate pattern**: Auto-create sessions on demand
4. **Indexed queries**: Fast filtering via SQL indexes
5. **JSON serialization**: Flexible schema for metadata and history
6. **Automatic cleanup**: Background service removes expired sessions
7. **Session warmup**: Pre-computed summaries for client consumption

**Performance Characteristics:**

- **Get**: O(1) for in-memory, O(log N) for SQLite (indexed)
- **List by agent**: O(log M) for SQLite (where M = sessions per agent)
- **Query**: O(N) for in-memory, O(log N) for SQLite (with indexes)
- **Save**: O(1) for in-memory, O(log N) for SQLite

**Best Practices:**

1. Use SQLite for production deployments
2. Enable WAL mode for better concurrency
3. Index frequently queried fields (agent_id, status, session_type)
4. Run cleanup service to prevent unbounded growth
5. Use session warmup to avoid N+1 queries in UI
