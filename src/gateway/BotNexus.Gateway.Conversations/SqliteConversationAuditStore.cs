using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// SQLite-backed audit store for conversation mutations.
/// Creates a <c>conversation_audit</c> table in the existing sessions/conversations database.
/// </summary>
public sealed class SqliteConversationAuditStore : IConversationAuditStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteConversationAuditStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteConversationAuditStore(string connectionString, ILogger<SqliteConversationAuditStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RecordAsync(ConversationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversation_audit (conversation_id, agent_id, action, actor, source, previous_value, new_value, timestamp)
            VALUES (@conversationId, @agentId, @action, @actor, @source, @previousValue, @newValue, @timestamp)
            """;
        cmd.Parameters.AddWithValue("@conversationId", entry.ConversationId);
        cmd.Parameters.AddWithValue("@agentId", entry.AgentId);
        cmd.Parameters.AddWithValue("@action", entry.Action);
        cmd.Parameters.AddWithValue("@actor", entry.Actor);
        cmd.Parameters.AddWithValue("@source", entry.Source);
        cmd.Parameters.AddWithValue("@previousValue", (object?)Truncate(entry.PreviousValue) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@newValue", (object?)Truncate(entry.NewValue) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToString("O"));

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Conversation {ConversationId} {Action} by {Actor} via {Source}",
            entry.ConversationId, entry.Action, entry.Actor, entry.Source);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConversationAuditEntry>> GetByConversationAsync(
        string conversationId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, conversation_id, agent_id, action, actor, source, previous_value, new_value, timestamp
            FROM conversation_audit
            WHERE conversation_id = @conversationId
            ORDER BY id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@conversationId", conversationId);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var results = new List<ConversationAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ConversationAuditEntry
            {
                Id = reader.GetInt64(0),
                ConversationId = reader.GetString(1),
                AgentId = reader.GetString(2),
                Action = reader.GetString(3),
                Actor = reader.GetString(4),
                Source = reader.GetString(5),
                PreviousValue = reader.IsDBNull(6) ? null : reader.GetString(6),
                NewValue = reader.IsDBNull(7) ? null : reader.GetString(7),
                Timestamp = DateTimeOffset.Parse(reader.GetString(8))
            });
        }

        return results;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS conversation_audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    source TEXT NOT NULL,
                    previous_value TEXT,
                    new_value TEXT,
                    timestamp TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_conversation_audit_conversation_id
                    ON conversation_audit (conversation_id);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static string? Truncate(string? value)
        => value is null ? null : value.Length > 200 ? value[..200] : value;
}
