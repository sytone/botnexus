using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// SQLite-backed conversation audit log. Uses the same database as the conversation store.
/// </summary>
public sealed class SqliteConversationAuditLog : IConversationAuditLog, IDisposable
{
    private readonly string _connectionString;
    private int _initialized;

    public SqliteConversationAuditLog(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Dispose()
    {
        // Release any pooled connections so the database file can be deleted in test scenarios.
        using var conn = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(conn);
    }

    private void EnsureInitialized()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS conversation_audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    source TEXT NOT NULL,
                    previous_value TEXT,
                    new_value TEXT,
                    timestamp TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_conversation_audit_conversation_id
                    ON conversation_audit(conversation_id, timestamp DESC);
                """;
            command.ExecuteNonQuery();
        }
    }

    public async Task LogAsync(ConversationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_audit (conversation_id, action, actor, source, previous_value, new_value, timestamp)
            VALUES (@conversationId, @action, @actor, @source, @previousValue, @newValue, @timestamp)
            """;
        command.Parameters.AddWithValue("@conversationId", entry.ConversationId);
        command.Parameters.AddWithValue("@action", entry.Action);
        command.Parameters.AddWithValue("@actor", entry.Actor);
        command.Parameters.AddWithValue("@source", entry.Source);
        command.Parameters.AddWithValue("@previousValue", (object?)entry.PreviousValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@newValue", (object?)entry.NewValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationAuditEntry>> GetAsync(string conversationId, int limit = 50, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id, action, actor, source, previous_value, new_value, timestamp
            FROM conversation_audit
            WHERE conversation_id = @conversationId
            ORDER BY timestamp DESC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@conversationId", conversationId);
        command.Parameters.AddWithValue("@limit", limit);

        var entries = new List<ConversationAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ConversationAuditEntry
            {
                ConversationId = reader.GetString(0),
                Action = reader.GetString(1),
                Actor = reader.GetString(2),
                Source = reader.GetString(3),
                PreviousValue = reader.IsDBNull(4) ? null : reader.GetString(4),
                NewValue = reader.IsDBNull(5) ? null : reader.GetString(5),
                Timestamp = DateTimeOffset.Parse(reader.GetString(6))
            });
        }

        return entries;
    }
}
