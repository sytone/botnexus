# Session Stores and Persistence

This document describes BotNexus's session storage architecture, including persistence strategies, query patterns, and lifecycle management.

## Overview

Sessions are persisted via `ISessionStore`, which supports multiple backend implementations:

- **InMemorySessionStore**: Non-durable, for testing
- **FileSessionStore**: JSON files, simple deployments
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
    Task DeleteAsync(SessionId sessionId, CancellationToken ct = default);
    
    // Queries
    Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId agentId, CancellationToken ct = default);
    Task<IReadOnlyList<GatewaySession>> QueryAsync(ExistenceQuery query, CancellationToken ct = default);
    
    // Lifecycle
    Task ArchiveAsync(SessionId sessionId, CancellationToken ct = default);
    Task<int> CleanupExpiredAsync(TimeSpan retention, CancellationToken ct = default);
}
```

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

Abstract base class that implements common logic: `GetOrCreateAsync` (auto-create sessions on demand), `ArchiveAsync` (seal sessions). Defines abstract methods (`GetInternalAsync`, `SaveInternalAsync`, `DeleteInternalAsync`, `ListInternalAsync`) for subclasses to implement storage-specific behavior.

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

**Characteristics:**

- Durable (survives restarts)
- Simple deployment (no database)
- File per session (inefficient for large counts)
- No cross-process concurrency (file locking)

**Storage Layout:**

```
~/.botnexus/sessions/
├── {sessionId}.json
├── {sessionId}.json
└── {sessionId}.json
```

**Key behaviors:** Atomic writes via temp file + rename, JSON serialization with camelCase naming, one file per session. Skips `.tmp` files during enumeration and logs warnings for corrupt session files.

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
CREATE TABLE sessions (
    session_id TEXT PRIMARY KEY,
    agent_id TEXT NOT NULL,
    session_type TEXT NOT NULL,
    status TEXT NOT NULL,
    channel_type TEXT,
    caller_id TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    expires_at TEXT,
    participants_json TEXT,
    history_json TEXT,
    metadata_json TEXT
);

CREATE INDEX idx_sessions_agent_id ON sessions(agent_id);
CREATE INDEX idx_sessions_status ON sessions(status);
CREATE INDEX idx_sessions_session_type ON sessions(session_type);
CREATE INDEX idx_sessions_created_at ON sessions(created_at);
CREATE INDEX idx_sessions_expires_at ON sessions(expires_at) WHERE expires_at IS NOT NULL;
```

**Key behaviors:** Parameterized queries throughout, `INSERT OR REPLACE` for upserts, JSON serialization for complex fields (`participants_json`, `history_json`, `metadata_json`), lazy schema initialization via `EnsureSchemaAsync`, and ISO 8601 date formatting.

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
// Find all active UserAgent sessions for an agent
var query = new ExistenceQuery
{
    AgentId = AgentId.From("my-agent"),
    SessionType = SessionType.UserAgent,
    Status = SessionStatus.Active
};
var sessions = await _sessionStore.QueryAsync(query, ct);

// Find all soul sessions created in the last 7 days
var query = new ExistenceQuery
{
    SessionType = SessionType.Soul,
    CreatedAfter = DateTimeOffset.UtcNow.AddDays(-7),
    Limit = 100
};
var recentSoulSessions = await _sessionStore.QueryAsync(query, ct);
```

## Session Cleanup

**Automatic Cleanup:**

`CleanupExpiredAsync` deletes sessions with expired status and `updated_at` older than the retention cutoff. See [SqliteSessionStore.cs](../../src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs)

**SessionCleanupService:**

`BackgroundService` that periodically calls `CleanupExpiredAsync` with configurable interval and retention period. Logs results and handles errors with 5-minute retry backoff.

See [SessionCleanupService.cs](../../src/gateway/BotNexus.Gateway/SessionCleanupService.cs)

**SessionCleanupOptions:**

```csharp
public record SessionCleanupOptions
{
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(30);
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
| File | Durable | Moderate | Single process | Simple deployments |
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
- **List by agent**: O(N) for file store, O(log M) for SQLite (where M = sessions per agent)
- **Query**: O(N) for in-memory/file, O(log N) for SQLite (with indexes)
- **Save**: O(1) for in-memory, O(1) disk I/O for file, O(log N) for SQLite

**Best Practices:**

1. Use SQLite for production deployments
2. Enable WAL mode for better concurrency
3. Index frequently queried fields (agent_id, status, session_type)
4. Run cleanup service to prevent unbounded growth
5. Use session warmup to avoid N+1 queries in UI
6. Archive old sessions instead of deleting (for audit trail)
