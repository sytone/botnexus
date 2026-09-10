using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// SQLite-backed <see cref="IPushSubscriptionStore"/>, in its own file beside the other stores.
/// </summary>
/// <remarks>
/// Kept apart from notifications.sqlite deliberately. Notifications are transient records that get
/// read and deleted; subscriptions are long-lived device registrations whose loss silently stops
/// delivery to a phone that has no way to know. Separate files means clearing one cannot take the
/// other with it.
/// </remarks>
public sealed class SqlitePushSubscriptionStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    TimeProvider? timeProvider = null) : IPushSubscriptionStore
{
    private readonly string _dbPath = dbPath;
    private readonly SqliteWalMaintenance _walMaintenance = new(fileSystem);
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
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
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS push_subscriptions (
                    endpoint TEXT PRIMARY KEY,
                    p256dh TEXT NOT NULL,
                    auth TEXT NOT NULL,
                    user_agent TEXT NULL,
                    created_at TEXT NOT NULL,
                    last_success_at TEXT NULL
                );
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
    public async Task SaveAsync(PushSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            // The keys are replaced but created_at is kept: a re-subscribe with the same endpoint
            // is the same device continuing, not a new one.
            command.CommandText = """
                INSERT INTO push_subscriptions (endpoint, p256dh, auth, user_agent, created_at)
                VALUES ($endpoint, $p256dh, $auth, $userAgent, $createdAt)
                ON CONFLICT(endpoint) DO UPDATE SET
                    p256dh = excluded.p256dh,
                    auth = excluded.auth,
                    user_agent = excluded.user_agent;
                """;
            command.Parameters.AddWithValue("$endpoint", subscription.Endpoint);
            command.Parameters.AddWithValue("$p256dh", subscription.P256dh);
            command.Parameters.AddWithValue("$auth", subscription.Auth);
            command.Parameters.AddWithValue("$userAgent", (object?)subscription.UserAgent ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", _timeProvider.GetUtcNow().ToString("O"));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PushSubscription>> ListAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM push_subscriptions ORDER BY created_at;";

        var results = new List<PushSubscription>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new PushSubscription
            {
                Endpoint = reader.GetString(reader.GetOrdinal("endpoint")),
                P256dh = reader.GetString(reader.GetOrdinal("p256dh")),
                Auth = reader.GetString(reader.GetOrdinal("auth")),
                UserAgent = Nullable(reader, "user_agent"),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                LastSuccessAtUtc = Nullable(reader, "last_success_at") is { } last
                    ? DateTimeOffset.Parse(last)
                    : null,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM push_subscriptions WHERE endpoint = $endpoint;";
            command.Parameters.AddWithValue("$endpoint", endpoint);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkDeliveredAsync(string endpoint, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE push_subscriptions SET last_success_at = $at WHERE endpoint = $endpoint;";
            command.Parameters.AddWithValue("$at", _timeProvider.GetUtcNow().ToString("O"));
            command.Parameters.AddWithValue("$endpoint", endpoint);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string? Nullable(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private SqliteConnection CreateConnection() => new(_connectionString);
}
