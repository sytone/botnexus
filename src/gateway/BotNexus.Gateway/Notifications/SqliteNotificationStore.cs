using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Notifications;

/// <summary>
/// SQLite-backed <see cref="INotificationStore"/>, following the nav-order store pattern.
/// </summary>
/// <remarks>
/// Server-side rather than in the browser, because the point of the feature is to tell someone what
/// an agent did while they were not watching - and "not watching" includes being on a different
/// device. Read state lives here for the same reason: dismissing something on a laptop should not
/// leave it unread on a phone.
/// </remarks>
public sealed class SqliteNotificationStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    TimeProvider? timeProvider = null,
    ILogger<SqliteNotificationStore>? logger = null) : INotificationStore
{
    private readonly string _dbPath = dbPath;
    private readonly SqliteWalMaintenance _walMaintenance = new(fileSystem);
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<SqliteNotificationStore> _logger = logger ?? NullLogger<SqliteNotificationStore>.Instance;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await _walMaintenance.ApplyJournalModeAsync(connection, _dbPath, cancellationToken: ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // created_at is stored as an ISO-8601 string so the ordering the UI needs is the
            // ordering SQLite already gives a TEXT column, and the value stays readable in the file.
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS notifications (
                    id TEXT PRIMARY KEY,
                    kind INTEGER NOT NULL,
                    severity INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    body TEXT NULL,
                    agent_id TEXT NULL,
                    conversation_id TEXT NULL,
                    link TEXT NULL,
                    created_at TEXT NOT NULL,
                    read_at TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_notifications_created_at
                    ON notifications (created_at DESC);
                CREATE INDEX IF NOT EXISTS ix_notifications_unread
                    ON notifications (read_at) WHERE read_at IS NULL;
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Notification> AppendAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        await InitializeAsync(ct).ConfigureAwait(false);

        // The caller supplies content; identity and timestamp belong to the store so two sources
        // cannot collide on an id or disagree about ordering.
        var stored = notification with
        {
            Id = string.IsNullOrWhiteSpace(notification.Id) ? Guid.NewGuid().ToString("N") : notification.Id,
            CreatedAtUtc = notification.CreatedAtUtc == default
                ? _timeProvider.GetUtcNow()
                : notification.CreatedAtUtc,
        };

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO notifications
                    (id, kind, severity, title, body, agent_id, conversation_id, link, created_at, read_at)
                VALUES
                    ($id, $kind, $severity, $title, $body, $agent, $conversation, $link, $created, $read);
                """;
            command.Parameters.AddWithValue("$id", stored.Id);
            command.Parameters.AddWithValue("$kind", (int)stored.Kind);
            command.Parameters.AddWithValue("$severity", (int)stored.Severity);
            command.Parameters.AddWithValue("$title", stored.Title);
            command.Parameters.AddWithValue("$body", (object?)stored.Body ?? DBNull.Value);
            command.Parameters.AddWithValue("$agent", (object?)stored.AgentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$conversation", (object?)stored.ConversationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$link", (object?)stored.Link ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", stored.CreatedAtUtc.UtcDateTime.ToString("o"));
            command.Parameters.AddWithValue("$read", (object?)stored.ReadAtUtc?.UtcDateTime.ToString("o") ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return stored;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Notification>> ListAsync(
        bool includeRead = true,
        int limit = 100,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeRead
            ? "SELECT * FROM notifications ORDER BY created_at DESC LIMIT $limit;"
            : "SELECT * FROM notifications WHERE read_at IS NULL ORDER BY created_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        List<Notification> results = [];
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Read(reader));

        return results;
    }

    /// <inheritdoc />
    public async Task<int> UnreadCountAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notifications WHERE read_at IS NULL;";

        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<bool> MarkReadAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        // Only unread rows are touched, so a second read does not move a timestamp that already
        // recorded when the notification was actually seen.
        return await ExecuteWriteAsync(
            "UPDATE notifications SET read_at = $now WHERE id = $id AND read_at IS NULL;",
            command =>
            {
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().UtcDateTime.ToString("o"));
            },
            ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public Task<int> MarkAllReadAsync(CancellationToken ct = default) =>
        ExecuteWriteAsync(
            "UPDATE notifications SET read_at = $now WHERE read_at IS NULL;",
            command => command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().UtcDateTime.ToString("o")),
            ct);

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        return await ExecuteWriteAsync(
            "DELETE FROM notifications WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", id),
            ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public Task<int> PruneReadAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = _timeProvider.GetUtcNow().Subtract(olderThan).UtcDateTime.ToString("o");

        return ExecuteWriteAsync(
            "DELETE FROM notifications WHERE read_at IS NOT NULL AND created_at < $cutoff;",
            command => command.Parameters.AddWithValue("$cutoff", cutoff),
            ct);
    }

    private async Task<int> ExecuteWriteAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind(command);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static Notification Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Kind = (NotificationKind)reader.GetInt32(reader.GetOrdinal("kind")),
        Severity = (NotificationSeverity)reader.GetInt32(reader.GetOrdinal("severity")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Body = GetNullableString(reader, "body"),
        AgentId = GetNullableString(reader, "agent_id"),
        ConversationId = GetNullableString(reader, "conversation_id"),
        Link = GetNullableString(reader, "link"),
        CreatedAtUtc = DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal("created_at")),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
        ReadAtUtc = GetNullableString(reader, "read_at") is { } read
            ? DateTimeOffset.Parse(
                read,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
            : null,
    };

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    // Via the factory, not `new`: it is where connection setup shared by every store lives, and a
    // store that opened its own raw connection would quietly miss it.
    private SqliteConnection CreateConnection() => SqliteConnectionFactory.Create(_connectionString);
}
