using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// SQLite-backed store for <see cref="WebhookRun"/> records.
/// Uses WAL mode and a write-lock semaphore for safe concurrent access.
/// </summary>
public sealed class SqliteWebhookRunStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    ILogger<SqliteWebhookRunStore>? logger = null) : IWebhookRunStore
{
    private readonly string _dbPath = dbPath;
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly ILogger<SqliteWebhookRunStore> _logger = logger ?? NullLogger<SqliteWebhookRunStore>.Instance;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS webhook_runs (
                    id TEXT PRIMARY KEY,
                    webhook_id TEXT NOT NULL,
                    conversation_id TEXT NULL,
                    session_id TEXT NULL,
                    status TEXT NOT NULL,
                    accepted_at TEXT NOT NULL,
                    started_at TEXT NULL,
                    completed_at TEXT NULL,
                    agent_response TEXT NULL,
                    error TEXT NULL,
                    callback_url TEXT NULL,
                    agent_action INTEGER NOT NULL DEFAULT 1
                );

                CREATE INDEX IF NOT EXISTS idx_webhook_runs_webhook_id_accepted_at
                ON webhook_runs(webhook_id, accepted_at DESC);
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WebhookRun> CreateAsync(WebhookRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO webhook_runs
                    (id, webhook_id, conversation_id, session_id, status, accepted_at, started_at, completed_at, agent_response, error, callback_url, agent_action)
                VALUES
                    ($id, $webhookId, $conversationId, $sessionId, $status, $acceptedAt, $startedAt, $completedAt, $agentResponse, $error, $callbackUrl, $agentAction)
                """;
            BindRun(cmd, run);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return run;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WebhookRun?> GetAsync(WebhookRunId runId, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, webhook_id, conversation_id, session_id, status, accepted_at, started_at, completed_at, agent_response, error, callback_url, agent_action
            FROM webhook_runs
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", runId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRun(reader) : null;
    }

    public async Task<WebhookRun> UpdateAsync(WebhookRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO webhook_runs
                    (id, webhook_id, conversation_id, session_id, status, accepted_at, started_at, completed_at, agent_response, error, callback_url, agent_action)
                VALUES
                    ($id, $webhookId, $conversationId, $sessionId, $status, $acceptedAt, $startedAt, $completedAt, $agentResponse, $error, $callbackUrl, $agentAction)
                ON CONFLICT(id) DO UPDATE SET
                    conversation_id = excluded.conversation_id,
                    session_id = excluded.session_id,
                    status = excluded.status,
                    started_at = excluded.started_at,
                    completed_at = excluded.completed_at,
                    agent_response = excluded.agent_response,
                    error = excluded.error
                """;
            BindRun(cmd, run);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return run;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<WebhookRun>> ListByWebhookAsync(
        WebhookId webhookId,
        int limit = 20,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, webhook_id, conversation_id, session_id, status, accepted_at, started_at, completed_at, agent_response, error, callback_url, agent_action
            FROM webhook_runs
            WHERE webhook_id = $webhookId
            ORDER BY accepted_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$webhookId", webhookId.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        List<WebhookRun> results = [];
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRun(reader));
        return results;
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM webhook_runs
                WHERE completed_at IS NOT NULL
                  AND completed_at < $cutoff
                  AND status IN ('Completed', 'Failed', 'Timeout')
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        // busy_timeout is per-connection and resets to 0 on every open, so it must be applied on
        // EVERY connection (operations open a fresh connection each time) - not just at init like
        // the database-level journal_mode=WAL. Without it a concurrent cross-process writer hits
        // SQLITE_BUSY immediately instead of waiting briefly for the lock to clear (#1450).
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Open)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }
        };
        return connection;
    }

    private static void BindRun(SqliteCommand cmd, WebhookRun r)
    {
        cmd.Parameters.AddWithValue("$id", r.Id.Value);
        cmd.Parameters.AddWithValue("$webhookId", r.WebhookId.Value);
        cmd.Parameters.AddWithValue("$conversationId", r.ConversationId.Value);
        cmd.Parameters.AddWithValue("$sessionId", r.SessionId.HasValue ? (object)r.SessionId.Value.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$status", r.Status.ToString());
        cmd.Parameters.AddWithValue("$acceptedAt", r.AcceptedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$startedAt", r.StartedAt.HasValue ? (object)r.StartedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$completedAt", r.CompletedAt.HasValue ? (object)r.CompletedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$agentResponse", (object?)r.AgentResponse ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)r.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$callbackUrl", (object?)r.CallbackUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agentAction", r.AgentAction ? 1 : 0);
    }

    private static WebhookRun ReadRun(SqliteDataReader reader)
    {
        return new WebhookRun
        {
            Id = WebhookRunId.From(reader.GetString(0)),
            WebhookId = WebhookId.From(reader.GetString(1)),
            ConversationId = ConversationId.From(reader.GetString(2)),
            SessionId = reader.IsDBNull(3) ? null : SessionId.From(reader.GetString(3)),
            Status = Enum.Parse<WebhookRunStatus>(reader.GetString(4)),
            AcceptedAt = ParseDate(reader.GetString(5)),
            StartedAt = reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
            CompletedAt = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
            AgentResponse = reader.IsDBNull(8) ? null : reader.GetString(8),
            Error = reader.IsDBNull(9) ? null : reader.GetString(9),
            CallbackUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
            AgentAction = !reader.IsDBNull(11) && reader.GetInt32(11) != 0
        };
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
}
