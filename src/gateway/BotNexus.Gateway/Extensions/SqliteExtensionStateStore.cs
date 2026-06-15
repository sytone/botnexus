using Microsoft.Data.Sqlite;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// SQLite-backed implementation of <see cref="Abstractions.Extensions.IExtensionStateStore"/>.
/// Uses WAL mode and a write lock for concurrent safety.
/// </summary>
public sealed class SqliteExtensionStateStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    ILogger<SqliteExtensionStateStore>? logger = null)
    : Abstractions.Extensions.IExtensionStateStore
{
    private readonly string _dbPath = dbPath;
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly ILogger<SqliteExtensionStateStore> _logger = logger ?? NullLogger<SqliteExtensionStateStore>.Instance;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

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

            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS extension_state (
                    extension_id TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (extension_id, key)
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialized = true;
            _logger.LogDebug("Extension state store initialized at {DbPath}.", _dbPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string?> GetAsync(string extensionId, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value
            FROM extension_state
            WHERE extension_id = $extensionId AND key = $key
            """;
        command.Parameters.AddWithValue("$extensionId", extensionId);
        command.Parameters.AddWithValue("$key", key);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task SetAsync(string extensionId, string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO extension_state (extension_id, key, value, updated_at)
                VALUES ($extensionId, $key, $value, $updatedAt)
                ON CONFLICT(extension_id, key) DO UPDATE SET
                    value = excluded.value,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$extensionId", extensionId);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsync(string extensionId, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM extension_state
                WHERE extension_id = $extensionId AND key = $key
                """;
            command.Parameters.AddWithValue("$extensionId", extensionId);
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string extensionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key
            FROM extension_state
            WHERE extension_id = $extensionId
            ORDER BY key
            """;
        command.Parameters.AddWithValue("$extensionId", extensionId);

        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            keys.Add(reader.GetString(0));

        return keys;
    }

    public async Task ClearAsync(string extensionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM extension_state
                WHERE extension_id = $extensionId
                """;
            command.Parameters.AddWithValue("$extensionId", extensionId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("Cleared all state for extension '{ExtensionId}'.", extensionId);
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
}
