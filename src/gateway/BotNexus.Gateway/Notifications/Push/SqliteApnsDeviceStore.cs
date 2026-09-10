using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// SQLite-backed <see cref="IApnsDeviceStore"/>, beside the web push subscriptions.
/// </summary>
/// <remarks>
/// Its own file for the same reason web push has one: these are long-lived device registrations
/// whose loss silently ends delivery to a phone that has no way to find out, and they must not be
/// collateral damage when a transient store is cleared.
/// </remarks>
public sealed class SqliteApnsDeviceStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    TimeProvider? timeProvider = null) : IApnsDeviceStore
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
                CREATE TABLE IF NOT EXISTS apns_devices (
                    device_token TEXT PRIMARY KEY,
                    environment TEXT NOT NULL,
                    device_name TEXT NULL,
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
    public async Task SaveAsync(ApnsDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            // created_at is kept across a re-register: iOS re-issues tokens on its own schedule,
            // and the same token arriving again is the same install continuing, not a new device.
            command.CommandText = """
                INSERT INTO apns_devices (device_token, environment, device_name, created_at)
                VALUES ($deviceToken, $environment, $deviceName, $createdAt)
                ON CONFLICT(device_token) DO UPDATE SET
                    environment = excluded.environment,
                    device_name = excluded.device_name;
                """;
            command.Parameters.AddWithValue("$deviceToken", device.DeviceToken);
            command.Parameters.AddWithValue("$environment", device.Environment);
            command.Parameters.AddWithValue("$deviceName", (object?)device.DeviceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", _timeProvider.GetUtcNow().ToString("O"));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApnsDevice>> ListAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM apns_devices ORDER BY created_at;";

        var results = new List<ApnsDevice>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new ApnsDevice
            {
                DeviceToken = reader.GetString(reader.GetOrdinal("device_token")),
                Environment = reader.GetString(reader.GetOrdinal("environment")),
                DeviceName = Nullable(reader, "device_name"),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                LastSuccessAtUtc = Nullable(reader, "last_success_at") is { } last
                    ? DateTimeOffset.Parse(last)
                    : null,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string deviceToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
            return false;

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM apns_devices WHERE device_token = $deviceToken;";
            command.Parameters.AddWithValue("$deviceToken", deviceToken);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkDeliveredAsync(string deviceToken, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE apns_devices SET last_success_at = $at WHERE device_token = $deviceToken;";
            command.Parameters.AddWithValue("$at", _timeProvider.GetUtcNow().ToString("O"));
            command.Parameters.AddWithValue("$deviceToken", deviceToken);

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
