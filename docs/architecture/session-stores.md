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

Abstract base class that implements common logic:

```csharp
public abstract class SessionStoreBase : ISessionStore
{
    public async Task<GatewaySession> GetOrCreateAsync(
        SessionId sessionId,
        AgentId agentId,
        CancellationToken ct)
    {
        var existing = await GetAsync(sessionId, ct);
        if (existing != null)
            return existing;
        
        var session = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            SessionType = SessionType.UserAgent,  // Default
            Status = SessionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        
        await SaveAsync(session, ct);
        return session;
    }
    
    public async Task ArchiveAsync(SessionId sessionId, CancellationToken ct)
    {
        var session = await GetAsync(sessionId, ct);
        if (session == null)
            return;
        
        session.Status = SessionStatus.Sealed;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(session, ct);
    }
    
    // Abstract methods for subclasses
    protected abstract Task<GatewaySession?> GetInternalAsync(SessionId sessionId, CancellationToken ct);
    protected abstract Task SaveInternalAsync(GatewaySession session, CancellationToken ct);
    protected abstract Task DeleteInternalAsync(SessionId sessionId, CancellationToken ct);
    protected abstract Task<IReadOnlyList<GatewaySession>> ListInternalAsync(AgentId agentId, CancellationToken ct);
}
```

## InMemorySessionStore

**Characteristics:**

- Non-durable (lost on restart)
- Fast (in-process dictionary)
- Suitable for testing and development
- No concurrency safety across processes

**Implementation:**

```csharp
public sealed class InMemorySessionStore : SessionStoreBase
{
    private readonly ConcurrentDictionary<string, GatewaySession> _sessions = new();
    
    protected override Task<GatewaySession?> GetInternalAsync(
        SessionId sessionId,
        CancellationToken ct)
    {
        _sessions.TryGetValue(sessionId.Value, out var session);
        return Task.FromResult(session);
    }
    
    protected override Task SaveInternalAsync(
        GatewaySession session,
        CancellationToken ct)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        _sessions[session.SessionId.Value] = session;
        return Task.CompletedTask;
    }
    
    protected override Task DeleteInternalAsync(
        SessionId sessionId,
        CancellationToken ct)
    {
        _sessions.TryRemove(sessionId.Value, out _);
        return Task.CompletedTask;
    }
    
    protected override Task<IReadOnlyList<GatewaySession>> ListInternalAsync(
        AgentId agentId,
        CancellationToken ct)
    {
        var sessions = _sessions.Values
            .Where(s => s.AgentId == agentId)
            .ToList();
        return Task.FromResult<IReadOnlyList<GatewaySession>>(sessions);
    }
}
```

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

**Implementation:**

```csharp
public sealed class FileSessionStore : SessionStoreBase
{
    private readonly string _baseDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public FileSessionStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        Directory.CreateDirectory(_baseDirectory);
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
    
    protected override async Task<GatewaySession?> GetInternalAsync(
        SessionId sessionId,
        CancellationToken ct)
    {
        var path = GetSessionPath(sessionId);
        if (!File.Exists(path))
            return null;
        
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<GatewaySession>(json, _jsonOptions);
    }
    
    protected override async Task SaveInternalAsync(
        GatewaySession session,
        CancellationToken ct)
    {
        var path = GetSessionPath(session.SessionId);
        var json = JsonSerializer.Serialize(session, _jsonOptions);
        
        // Atomic write via temp + rename
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
    }
    
    protected override Task DeleteInternalAsync(
        SessionId sessionId,
        CancellationToken ct)
    {
        var path = GetSessionPath(sessionId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }
    
    protected override async Task<IReadOnlyList<GatewaySession>> ListInternalAsync(
        AgentId agentId,
        CancellationToken ct)
    {
        var sessions = new List<GatewaySession>();
        
        foreach (var file in Directory.GetFiles(_baseDirectory, "*.json"))
        {
            if (file.EndsWith(".tmp"))
                continue;
            
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var session = JsonSerializer.Deserialize<GatewaySession>(json, _jsonOptions);
                if (session?.AgentId == agentId)
                    sessions.Add(session);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load session from {File}", file);
            }
        }
        
        return sessions;
    }
    
    private string GetSessionPath(SessionId sessionId)
        => Path.Combine(_baseDirectory, $"{sessionId.Value}.json");
}
```

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

**Implementation:**

```csharp
public sealed class SqliteSessionStore : SessionStoreBase
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public SqliteSessionStore(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
        EnsureSchemaAsync().GetAwaiter().GetResult();
    }
    
    protected override async Task<GatewaySession?> GetInternalAsync(
        SessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT session_id, agent_id, session_type, status, channel_type, caller_id,
                   created_at, updated_at, expires_at,
                   participants_json, history_json, metadata_json
            FROM sessions
            WHERE session_id = @sessionId";
        command.Parameters.AddWithValue("@sessionId", sessionId.Value);
        
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        
        return DeserializeSession(reader);
    }
    
    protected override async Task SaveInternalAsync(
        GatewaySession session,
        CancellationToken ct)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO sessions (
                session_id, agent_id, session_type, status, channel_type, caller_id,
                created_at, updated_at, expires_at,
                participants_json, history_json, metadata_json
            ) VALUES (
                @sessionId, @agentId, @sessionType, @status, @channelType, @callerId,
                @createdAt, @updatedAt, @expiresAt,
                @participantsJson, @historyJson, @metadataJson
            )";
        
        command.Parameters.AddWithValue("@sessionId", session.SessionId.Value);
        command.Parameters.AddWithValue("@agentId", session.AgentId.Value);
        command.Parameters.AddWithValue("@sessionType", session.SessionType.Value);
        command.Parameters.AddWithValue("@status", session.Status.Value);
        command.Parameters.AddWithValue("@channelType", session.ChannelType?.Value ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@callerId", session.CallerId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", session.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@updatedAt", session.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@expiresAt", session.ExpiresAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@participantsJson", JsonSerializer.Serialize(session.Participants, _jsonOptions));
        command.Parameters.AddWithValue("@historyJson", JsonSerializer.Serialize(session.History, _jsonOptions));
        command.Parameters.AddWithValue("@metadataJson", JsonSerializer.Serialize(session.Metadata, _jsonOptions));
        
        await command.ExecuteNonQueryAsync(ct);
    }
    
    protected override async Task<IReadOnlyList<GatewaySession>> ListInternalAsync(
        AgentId agentId,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT session_id, agent_id, session_type, status, channel_type, caller_id,
                   created_at, updated_at, expires_at,
                   participants_json, history_json, metadata_json
            FROM sessions
            WHERE agent_id = @agentId
            ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("@agentId", agentId.Value);
        
        var sessions = new List<GatewaySession>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(DeserializeSession(reader));
        }
        
        return sessions;
    }
}
```

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

```csharp
public async Task<IReadOnlyList<GatewaySession>> QueryAsync(
    ExistenceQuery query,
    CancellationToken ct)
{
    var sql = new StringBuilder("SELECT * FROM sessions WHERE 1=1");
    var parameters = new List<SqliteParameter>();
    
    if (query.AgentId is not null)
    {
        sql.Append(" AND agent_id = @agentId");
        parameters.Add(new SqliteParameter("@agentId", query.AgentId.Value));
    }
    
    if (query.SessionType is not null)
    {
        sql.Append(" AND session_type = @sessionType");
        parameters.Add(new SqliteParameter("@sessionType", query.SessionType.Value));
    }
    
    if (query.Status is not null)
    {
        sql.Append(" AND status = @status");
        parameters.Add(new SqliteParameter("@status", query.Status.Value));
    }
    
    if (query.ChannelType is not null)
    {
        sql.Append(" AND channel_type = @channelType");
        parameters.Add(new SqliteParameter("@channelType", query.ChannelType.Value));
    }
    
    if (query.CreatedAfter is not null)
    {
        sql.Append(" AND created_at >= @createdAfter");
        parameters.Add(new SqliteParameter("@createdAfter", query.CreatedAfter.Value.ToString("O")));
    }
    
    if (query.CreatedBefore is not null)
    {
        sql.Append(" AND created_at <= @createdBefore");
        parameters.Add(new SqliteParameter("@createdBefore", query.CreatedBefore.Value.ToString("O")));
    }
    
    sql.Append(" ORDER BY updated_at DESC");
    
    if (query.Limit is not null)
    {
        sql.Append(" LIMIT @limit");
        parameters.Add(new SqliteParameter("@limit", query.Limit.Value));
    }
    
    // Execute query...
}
```

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

```csharp
public async Task<int> CleanupExpiredAsync(
    TimeSpan retention,
    CancellationToken ct)
{
    var cutoff = DateTimeOffset.UtcNow - retention;
    
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);
    
    await using var command = connection.CreateCommand();
    command.CommandText = @"
        DELETE FROM sessions
        WHERE status = @expired
          AND updated_at < @cutoff";
    command.Parameters.AddWithValue("@expired", "expired");
    command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
    
    return await command.ExecuteNonQueryAsync(ct);
}
```

**SessionCleanupService:**

Background service that periodically cleans up expired sessions:

```csharp
public sealed class SessionCleanupService : BackgroundService
{
    private readonly ISessionStore _sessionStore;
    private readonly IOptions<SessionCleanupOptions> _options;
    private readonly ILogger<SessionCleanupService> _logger;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await _sessionStore.CleanupExpiredAsync(
                    _options.Value.RetentionPeriod,
                    stoppingToken);
                
                if (deleted > 0)
                    _logger.LogInformation("Cleaned up {Count} expired sessions", deleted);
                
                await Task.Delay(_options.Value.CleanupInterval, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session cleanup failed");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}
```

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

**Implementation:**

```csharp
public async Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(
    CancellationToken ct)
{
    var agents = _agentRegistry.GetAll();
    var allSessions = new List<GatewaySession>();
    
    foreach (var agent in agents)
    {
        var sessions = await _sessionStore.ListAsync(agent.AgentId, ct);
        allSessions.AddRange(sessions);
    }
    
    // Filter to visible sessions
    var visible = allSessions
        .Where(s => s.Status is SessionStatus.Active or SessionStatus.Suspended)
        .Where(s => s.SessionType is SessionType.UserAgent or SessionType.Soul)
        .Where(s => s.ExpiresAt == null || s.ExpiresAt > DateTimeOffset.UtcNow)
        .OrderByDescending(s => s.UpdatedAt);
    
    return visible.Select(s => new SessionSummary
    {
        SessionId = s.SessionId.Value,
        AgentId = s.AgentId.Value,
        SessionType = s.SessionType.Value,
        Status = s.Status.Value,
        ChannelType = s.ChannelType?.Value,
        MessageCount = s.History.Count,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt
    }).ToList();
}
```

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
