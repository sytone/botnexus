using System.Text.Json;
using System.Diagnostics;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using MessageRole = BotNexus.Domain.Primitives.MessageRole;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using SessionParticipant = BotNexus.Domain.Primitives.SessionParticipant;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// SQLite-backed session store for single-node persistent gateway sessions.
/// Uses WAL journal mode and per-session <see cref="SemaphoreSlim"/> locks so
/// concurrent agents can read and write independent sessions without blocking each other.
/// The global initialisation lock (<c>_initLock</c>) is only held during the one-time
/// schema creation; all subsequent operations use per-session granularity.
/// </summary>
public sealed class SqliteSessionStore : SessionStoreBase
{
    private static readonly ActivitySource ActivitySource = new("BotNexus.Gateway");
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();
    private readonly ConcurrentDictionary<SessionId, GatewaySession> _cache = new();
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConversationStore? _conversationStore;
    private readonly ILogger<SqliteSessionStore> _logger;
    private readonly ISecretRedactor? _redactor;

    /// <summary>
    /// Initialises a new <see cref="SqliteSessionStore"/>.
    /// </summary>
    /// <param name="connectionString">SQLite connection string for the sessions database.</param>
    /// <param name="logger">Logger for diagnostic output including migration summaries.</param>
    /// <param name="conversationStore">
    /// When provided, a startup migration links any orphaned sessions (those with no
    /// <c>conversation_id</c>) to their agent's default conversation.
    /// </param>
    /// <param name="redactor">When provided, secrets in content are redacted before storage.</param>
    public SqliteSessionStore(
        string connectionString,
        ILogger<SqliteSessionStore> logger,
        IConversationStore? conversationStore = null,
        ISecretRedactor? redactor = null)
    {
        _connectionString = connectionString;
        _logger = logger;
        _conversationStore = conversationStore;
        _redactor = redactor;
    }

    /// <inheritdoc />
    public override async Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var sessionLock = GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(sessionId, out var cached))
                return cached;

            var loaded = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
                _cache[sessionId] = loaded;

            return loaded;
        }
        finally { sessionLock.Release(); }
    }

    /// <inheritdoc />
    public override async Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get_or_create", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.agent.id", agentId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var sessionLock = GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(sessionId, out var cached))
                return cached;

            var loaded = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                _cache[sessionId] = loaded;
                return loaded;
            }

            var session = CreateSession(sessionId, agentId, null, _redactor);
            _cache[sessionId] = session;
            return session;
        }
        finally { sessionLock.Release(); }
    }

    /// <inheritdoc />
    public override async Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.save", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", session.SessionId);
        activity?.SetTag("botnexus.agent.id", session.AgentId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var sessionLock = GetSessionLock(session.SessionId);
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache[session.SessionId] = session;
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await UpsertSessionAsync(connection, session, cancellationToken).ConfigureAwait(false);
            await ReplaceHistoryAsync(connection, session, cancellationToken).ConfigureAwait(false);
        }
        finally { sessionLock.Release(); }
    }

    /// <inheritdoc />
    public override async Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.delete", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var sessionLock = GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache.TryRemove(sessionId, out _);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var deleteHistory = connection.CreateCommand();
            deleteHistory.CommandText = "DELETE FROM session_history WHERE session_id = $sessionId";
            deleteHistory.Parameters.AddWithValue("$sessionId", sessionId.Value);
            await deleteHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var deleteSession = connection.CreateCommand();
            deleteSession.CommandText = "DELETE FROM sessions WHERE id = $sessionId";
            deleteSession.Parameters.AddWithValue("$sessionId", sessionId.Value);
            await deleteSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _sessionLocks.TryRemove(sessionId, out _);
        }
        finally { sessionLock.Release(); }
    }

    /// <inheritdoc />
    public override async Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var sessionLock = GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache.TryRemove(sessionId, out _);

            var session = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                session.Status = SessionStatus.Sealed;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                _cache[sessionId] = session;
                await using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await UpsertSessionAsync(connection, session, cancellationToken).ConfigureAwait(false);
                await ReplaceHistoryAsync(connection, session, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { sessionLock.Release(); }
    }

    protected override async Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync(CancellationToken cancellationToken)
    {
        // EnumerateSessionsAsync reads across all sessions — safe without per-session lock under WAL
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM sessions
            ORDER BY updated_at DESC
            """;

        var sessions = new List<GatewaySession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sessionId = SessionId.From(reader.GetString(0));
            var session = _cache.GetValueOrDefault(sessionId)
                ?? await LoadSessionAsync(connection, sessionId, _redactor, cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                _cache[sessionId] = session;
                sessions.Add(session);
            }
        }

        return sessions;
    }

    private async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return; // double-check after acquiring lock

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            await walCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT,
                    channel_type TEXT,
                    caller_id TEXT,
                    session_type TEXT,
                    participants_json TEXT,
                    status TEXT,
                    metadata TEXT,
                    created_at TEXT,
                    updated_at TEXT,
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
                    is_compaction_summary INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS idx_session_history_session_id ON session_history(session_id);
                CREATE INDEX IF NOT EXISTS idx_sessions_agent_id ON sessions(agent_id);
                CREATE INDEX IF NOT EXISTS idx_sessions_created_at ON sessions(created_at);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Migrate: add tool columns to existing databases
            await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

            // Migrate: link orphaned sessions to their agent's default conversation
            if (_conversationStore is not null)
                await MigrateOrphanedSessionsAsync(connection, _conversationStore, cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    private SemaphoreSlim GetSessionLock(SessionId sessionId)
        => _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));

    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var migration in new[]
                 {
                     ("tool_name", "TEXT"),
                     ("tool_call_id", "TEXT"),
                     ("is_compaction_summary", "INTEGER NOT NULL DEFAULT 0"),
                     ("tool_args", "TEXT"),
                     ("tool_is_error", "INTEGER NOT NULL DEFAULT 0")
                 })
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE session_history ADD COLUMN {migration.Item1} {migration.Item2}";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) { /* column already exists */ }
        }

        foreach (var migration in new[]
                 {
                     ("session_type", "TEXT"),
                     ("participants_json", "TEXT"),
                     ("conversation_id", "TEXT")
                 })
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE sessions ADD COLUMN {migration.Item1} {migration.Item2}";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) { /* column already exists */ }
        }

        await using var renameStatus = connection.CreateCommand();
        renameStatus.CommandText = """
            UPDATE sessions
            SET status = 'Sealed'
            WHERE lower(status) = 'closed'
            """;
        await renameStatus.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds every session with no <c>conversation_id</c>, groups them by agent, and
    /// links each group to the agent's default conversation.  The most recently updated
    /// orphaned session becomes <see cref="Conversation.ActiveSessionId"/>.
    /// Safe to run on every startup — no-op when there are no orphaned rows.
    /// </summary>
    private async Task MigrateOrphanedSessionsAsync(
        SqliteConnection connection,
        IConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        // Collect distinct agents that have at least one orphaned session.
        await using var agentCmd = connection.CreateCommand();
        agentCmd.CommandText = """
            SELECT DISTINCT agent_id
            FROM sessions
            WHERE conversation_id IS NULL
              AND agent_id IS NOT NULL
            """;

        var agents = new List<string>();
        await using (var agentReader = await agentCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await agentReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                agents.Add(agentReader.GetString(0));
        }

        if (agents.Count == 0)
            return;

        var totalMigrated = 0;

        foreach (var agentIdValue in agents)
        {
            var agentId = AgentId.From(agentIdValue);

            // Find or create a named fallback conversation for orphaned sessions.
            // Using a legacy-named conversation keeps these clearly separate from real conversations.
            var existing = await conversationStore.ListAsync(agentId, cancellationToken).ConfigureAwait(false);
            var legacyTitle = $"legacy:{agentIdValue}";
            var conversation = existing.FirstOrDefault(c =>
                    c.Title == legacyTitle &&
                    c.Status == BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active)
                ?? await conversationStore.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation
                {
                    ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(),
                    AgentId = agentId,
                    Title = legacyTitle,
                    IsDefault = false
                }, cancellationToken).ConfigureAwait(false);

            var convIdValue = conversation.ConversationId.Value;

            // Find the most recently updated orphaned session for this agent.
            await using var latestCmd = connection.CreateCommand();
            latestCmd.CommandText = """
                SELECT id
                FROM sessions
                WHERE conversation_id IS NULL
                  AND agent_id = $agentId
                ORDER BY updated_at DESC
                LIMIT 1
                """;
            latestCmd.Parameters.AddWithValue("$agentId", agentIdValue);
            var latestSessionId = (string?)await latestCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            // Bulk-update all orphaned sessions for this agent.
            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE sessions
                SET conversation_id = $convId
                WHERE conversation_id IS NULL
                  AND agent_id = $agentId
                """;
            updateCmd.Parameters.AddWithValue("$convId", convIdValue);
            updateCmd.Parameters.AddWithValue("$agentId", agentIdValue);
            var count = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            totalMigrated += count;

            // Point the conversation at the most recently active orphaned session,
            // but only if it has no active session already.
            if (latestSessionId is not null && conversation.ActiveSessionId is null)
            {
                conversation.ActiveSessionId = SessionId.From(latestSessionId);
                await conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation(
            "Orphaned session migration: linked {Count} session(s) across {AgentCount} agent(s) to their default conversations.",
            totalMigrated, agents.Count);
    }

    private async Task<GatewaySession?> LoadSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadSessionAsync(connection, sessionId, _redactor, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GatewaySession?> LoadSessionAsync(SqliteConnection connection, SessionId sessionId, ISecretRedactor? redactor, CancellationToken cancellationToken)
    {
        await using var sessionCommand = connection.CreateCommand();
        sessionCommand.CommandText = """
            SELECT id, agent_id, channel_type, caller_id, session_type, participants_json, status, metadata, created_at, updated_at, conversation_id
            FROM sessions
            WHERE id = $sessionId
            """;
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var reader = await sessionCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var createdAt = ParseTimestamp(reader.GetString(8));
        var updatedAt = ParseTimestamp(reader.GetString(9));
        var metadata = DeserializeMetadata(reader.IsDBNull(7) ? null : reader.GetString(7));
        var status = ParseStatus(reader.IsDBNull(6) ? null : reader.GetString(6));
        ChannelKey? channelType = default;
        if (!reader.IsDBNull(2))
            channelType = ChannelKey.From(reader.GetString(2));
        var sessionType = ParseSessionType(reader.IsDBNull(4) ? null : reader.GetString(4), sessionId, channelType);
        var participants = DeserializeParticipants(reader.IsDBNull(5) ? null : reader.GetString(5));
        var agentIdValue = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (string.IsNullOrWhiteSpace(agentIdValue))
            return null;

        ConversationId? conversationId = null;
        if (reader.FieldCount > 10 && !reader.IsDBNull(10))
            conversationId = ConversationId.From(reader.GetString(10));

        var domainSession = new Session
        {
            SessionId = SessionId.From(reader.GetString(0)),
            AgentId = AgentId.From(agentIdValue),
            ChannelType = channelType,
            SessionType = sessionType,
            Participants = participants,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Metadata = metadata,
            ConversationId = conversationId
        };
        var session = new GatewaySession(domainSession, redactor)
        {
            CallerId = reader.IsDBNull(3) ? null : reader.GetString(3)
        };

        await reader.DisposeAsync().ConfigureAwait(false);

        await using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText = """
            SELECT role, content, timestamp, tool_name, tool_call_id, is_compaction_summary, tool_args, tool_is_error
            FROM session_history
            WHERE session_id = $sessionId
            ORDER BY id ASC
            """;
        historyCommand.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var historyReader = await historyCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<SessionEntry>();
        while (await historyReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new SessionEntry
            {
                Role = MessageRole.FromString(historyReader.IsDBNull(0) ? "user" : historyReader.GetString(0)),
                Content = historyReader.IsDBNull(1) ? string.Empty : historyReader.GetString(1),
                Timestamp = ParseTimestamp(historyReader.IsDBNull(2) ? null : historyReader.GetString(2)),
                ToolName = historyReader.IsDBNull(3) ? null : historyReader.GetString(3),
                ToolCallId = historyReader.IsDBNull(4) ? null : historyReader.GetString(4),
                IsCompactionSummary = !historyReader.IsDBNull(5) && historyReader.GetInt64(5) != 0,
                ToolArgs = historyReader.FieldCount > 6 && !historyReader.IsDBNull(6) ? historyReader.GetString(6) : null,
                ToolIsError = historyReader.FieldCount > 7 && !historyReader.IsDBNull(7) && historyReader.GetInt64(7) != 0
            });
        }

        var lastCompactionIndex = entries.FindLastIndex(entry => entry.IsCompactionSummary);
        if (lastCompactionIndex >= 0)
            entries = entries.GetRange(lastCompactionIndex, entries.Count - lastCompactionIndex);

        if (entries.Count > 0)
            session.AddEntries(entries);

        session.UpdatedAt = updatedAt;
        return session;
    }

    private static async Task UpsertSessionAsync(SqliteConnection connection, GatewaySession session, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (id, agent_id, channel_type, caller_id, session_type, participants_json, status, metadata, created_at, updated_at, conversation_id)
            VALUES ($id, $agentId, $channelType, $callerId, $sessionType, $participantsJson, $status, $metadata, $createdAt, $updatedAt, $conversationId)
            ON CONFLICT(id) DO UPDATE SET
                agent_id = excluded.agent_id,
                channel_type = excluded.channel_type,
                caller_id = excluded.caller_id,
                session_type = excluded.session_type,
                participants_json = excluded.participants_json,
                status = excluded.status,
                metadata = excluded.metadata,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at,
                conversation_id = excluded.conversation_id
            """;
        command.Parameters.AddWithValue("$id", session.SessionId.Value);
        command.Parameters.AddWithValue("$agentId", session.AgentId.Value);
        command.Parameters.AddWithValue("$channelType", session.ChannelType.HasValue ? session.ChannelType.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("$callerId", (object?)session.CallerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionType", session.SessionType.Value);
        command.Parameters.AddWithValue("$participantsJson", JsonSerializer.Serialize(session.Participants, JsonOptions));
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(session.Metadata, JsonOptions));
        command.Parameters.AddWithValue("$createdAt", session.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", session.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$conversationId", (object?)session.Session.ConversationId?.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceHistoryAsync(SqliteConnection connection, GatewaySession session, CancellationToken cancellationToken)
    {
        await using var dbTransaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)dbTransaction;

        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM session_history WHERE session_id = $sessionId";
        deleteCommand.Parameters.AddWithValue("$sessionId", session.SessionId.Value);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in session.GetHistorySnapshot())
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO session_history (session_id, role, content, timestamp, tool_name, tool_call_id, is_compaction_summary, tool_args, tool_is_error)
                VALUES ($sessionId, $role, $content, $timestamp, $toolName, $toolCallId, $isCompactionSummary, $toolArgs, $toolIsError)
                """;
            insertCommand.Parameters.AddWithValue("$sessionId", session.SessionId.Value);
            insertCommand.Parameters.AddWithValue("$role", entry.Role.Value);
            insertCommand.Parameters.AddWithValue("$content", entry.Content);
            insertCommand.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("O"));
            insertCommand.Parameters.AddWithValue("$toolName", (object?)entry.ToolName ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$toolCallId", (object?)entry.ToolCallId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$isCompactionSummary", entry.IsCompactionSummary ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$toolArgs", (object?)entry.ToolArgs ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$toolIsError", entry.ToolIsError ? 1 : 0);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static DateTimeOffset ParseTimestamp(string? timestamp)
        => DateTimeOffset.TryParse(timestamp, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static SessionStatus ParseStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "closed" => SessionStatus.Sealed,
            _ when Enum.TryParse<SessionStatus>(status, ignoreCase: true, out var parsed) => parsed,
            _ => SessionStatus.Active
        };

    private static SessionType ParseSessionType(string? raw, SessionId sessionId, ChannelKey? channelType)
    {
        if (!string.IsNullOrWhiteSpace(raw))
            return SessionType.FromString(raw);

        return InferSessionType(sessionId, channelType);
    }

    private static List<SessionParticipant> DeserializeParticipants(string? participantsJson)
    {
        if (string.IsNullOrWhiteSpace(participantsJson))
            return [];

        return JsonSerializer.Deserialize<List<SessionParticipant>>(participantsJson, JsonOptions) ?? [];
    }

    private static Dictionary<string, object?> DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, JsonOptions) ?? [];
    }

}
