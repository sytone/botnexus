using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// SQLite-backed store for <see cref="WebhookRegistration"/> records.
/// Uses WAL mode and a write-lock semaphore for safe concurrent access.
/// </summary>
public sealed class SqliteWebhookRegistrationStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    ILogger<SqliteWebhookRegistrationStore>? logger = null) : IWebhookRegistrationStore
{
    private readonly string _dbPath = dbPath;
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly ILogger<SqliteWebhookRegistrationStore> _logger = logger ?? NullLogger<SqliteWebhookRegistrationStore>.Instance;
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

                CREATE TABLE IF NOT EXISTS webhook_registrations (
                    id TEXT PRIMARY KEY,
                    label TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    pinned_conversation_id TEXT NULL,
                    secret TEXT NOT NULL,
                    default_response_mode TEXT NOT NULL DEFAULT 'Async',
                    enabled INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    last_used_at TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_webhook_registrations_agent_id
                ON webhook_registrations(agent_id);
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WebhookRegistration> CreateAsync(WebhookRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await InitializeAsync(ct).ConfigureAwait(false);

        var record = registration with
        {
            CreatedAt = registration.CreatedAt == default ? DateTimeOffset.UtcNow : registration.CreatedAt
        };

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO webhook_registrations
                    (id, label, agent_id, pinned_conversation_id, secret, default_response_mode, enabled, created_at, last_used_at)
                VALUES
                    ($id, $label, $agentId, $pinnedConversationId, $secret, $defaultResponseMode, $enabled, $createdAt, $lastUsedAt)
                """;
            BindRegistration(cmd, record);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Created webhook registration '{WebhookId}' for agent '{AgentId}'.", record.Id, record.AgentId);
            return record;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WebhookRegistration?> GetAsync(WebhookId webhookId, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, agent_id, pinned_conversation_id, secret, default_response_mode, enabled, created_at, last_used_at
            FROM webhook_registrations
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", webhookId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRegistration(reader) : null;
    }

    public async Task<IReadOnlyList<WebhookRegistration>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, agent_id, pinned_conversation_id, secret, default_response_mode, enabled, created_at, last_used_at
            FROM webhook_registrations
            WHERE $agentId IS NULL OR agent_id = $agentId
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("$agentId", agentId.HasValue ? (object)agentId.Value.Value : DBNull.Value);

        List<WebhookRegistration> results = [];
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRegistration(reader));
        return results;
    }

    public async Task<WebhookRegistration> UpdateAsync(WebhookRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO webhook_registrations
                    (id, label, agent_id, pinned_conversation_id, secret, default_response_mode, enabled, created_at, last_used_at)
                VALUES
                    ($id, $label, $agentId, $pinnedConversationId, $secret, $defaultResponseMode, $enabled, $createdAt, $lastUsedAt)
                ON CONFLICT(id) DO UPDATE SET
                    label = excluded.label,
                    agent_id = excluded.agent_id,
                    pinned_conversation_id = excluded.pinned_conversation_id,
                    secret = excluded.secret,
                    default_response_mode = excluded.default_response_mode,
                    enabled = excluded.enabled,
                    created_at = excluded.created_at,
                    last_used_at = excluded.last_used_at
                """;
            BindRegistration(cmd, registration);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Updated webhook registration '{WebhookId}'.", registration.Id);
            return registration;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsync(WebhookId webhookId, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM webhook_registrations WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", webhookId.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Deleted webhook registration '{WebhookId}'.", webhookId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConversationId?> TryPinConversationAsync(
        WebhookId webhookId,
        ConversationId conversationId,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var cas = connection.CreateCommand();
            cas.CommandText = """
                UPDATE webhook_registrations
                SET pinned_conversation_id = $conversationId
                WHERE id = $webhookId AND pinned_conversation_id IS NULL
                """;
            cas.Parameters.AddWithValue("$conversationId", conversationId.Value);
            cas.Parameters.AddWithValue("$webhookId", webhookId.Value);
            await cas.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT pinned_conversation_id FROM webhook_registrations WHERE id = $webhookId";
            read.Parameters.AddWithValue("$webhookId", webhookId.Value);
            var result = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (result is null or DBNull) return null;
            return ConversationId.From((string)result);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static void BindRegistration(SqliteCommand cmd, WebhookRegistration r)
    {
        cmd.Parameters.AddWithValue("$id", r.Id.Value);
        cmd.Parameters.AddWithValue("$label", r.Label);
        cmd.Parameters.AddWithValue("$agentId", r.AgentId.Value);
        cmd.Parameters.AddWithValue("$pinnedConversationId", r.PinnedConversationId.HasValue ? (object)r.PinnedConversationId.Value.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$secret", r.Secret);
        cmd.Parameters.AddWithValue("$defaultResponseMode", r.DefaultResponseMode.ToString());
        cmd.Parameters.AddWithValue("$enabled", r.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", r.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$lastUsedAt", r.LastUsedAt.HasValue ? (object)r.LastUsedAt.Value.ToString("O") : DBNull.Value);
    }

    private static WebhookRegistration ReadRegistration(SqliteDataReader reader)
    {
        return new WebhookRegistration
        {
            Id = WebhookId.From(reader.GetString(0)),
            Label = reader.GetString(1),
            AgentId = AgentId.From(reader.GetString(2)),
            PinnedConversationId = reader.IsDBNull(3) ? null : ConversationId.From(reader.GetString(3)),
            Secret = reader.GetString(4),
            DefaultResponseMode = Enum.Parse<WebhookResponseMode>(reader.GetString(5)),
            Enabled = !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
            CreatedAt = ParseDate(reader.GetString(7)),
            LastUsedAt = reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8))
        };
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
}
