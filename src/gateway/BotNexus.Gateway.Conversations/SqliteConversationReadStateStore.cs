using BotNexus.Domain.Primitives;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Durable SQLite implementation using one primary-key row per world-reader-conversation key and
/// a conditional upsert so concurrent processes cannot move a cursor backward.
/// </summary>
public sealed class SqliteConversationReadStateStore : IConversationReadStateStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    /// <summary>Creates a store using the supplied SQLite connection string.</summary>
    public SqliteConversationReadStateStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<ConversationReadState?> GetAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        ConversationReadStateKey.Validate(readerId, conversationId);
        var normalizedWorldId = ConversationReadStateKey.NormalizeWorldId(worldId);
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(
            connection, normalizedWorldId, readerId, conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConversationReadState> AdvanceAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        ConversationReadPosition position,
        CancellationToken cancellationToken = default)
    {
        ConversationReadStateKey.Validate(readerId, conversationId);
        var normalizedWorldId = ConversationReadStateKey.NormalizeWorldId(worldId);
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_read_state
                (world_id, reader_id, conversation_id, position, version)
            VALUES ($worldId, $readerId, $conversationId, $position, 1)
            ON CONFLICT(world_id, reader_id, conversation_id) DO UPDATE SET
                position = excluded.position,
                version = conversation_read_state.version + 1
            WHERE excluded.position > conversation_read_state.position;
            """;
        BindKey(command, normalizedWorldId, readerId, conversationId);
        command.Parameters.AddWithValue("$position", position.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return await ReadAsync(
            connection, normalizedWorldId, readerId, conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The read-state upsert completed without a stored row.");
    }

    private async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS conversation_read_state (
                    world_id TEXT NOT NULL,
                    reader_id TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    position INTEGER NOT NULL,
                    version INTEGER NOT NULL,
                    PRIMARY KEY (world_id, reader_id, conversation_id)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.Create(_connectionString);

    private static async Task<ConversationReadState?> ReadAsync(
        SqliteConnection connection,
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT position, version
            FROM conversation_read_state
            WHERE world_id = $worldId
              AND reader_id = $readerId
              AND conversation_id = $conversationId;
            """;
        BindKey(command, worldId, readerId, conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new ConversationReadState(
            worldId,
            readerId,
            conversationId,
            ConversationReadPosition.From(reader.GetInt64(0)),
            reader.GetInt64(1));
    }

    private static void BindKey(
        SqliteCommand command,
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId)
    {
        command.Parameters.AddWithValue("$worldId", worldId);
        command.Parameters.AddWithValue("$readerId", readerId.Value);
        command.Parameters.AddWithValue("$conversationId", conversationId.Value);
    }

}
