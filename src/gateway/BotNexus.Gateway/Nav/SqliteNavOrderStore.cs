using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Nav;

/// <summary>
/// SQLite-backed <see cref="INavOrderStore"/>. Persists per-user portal nav-order overrides to a
/// database file so they survive gateway restarts and roam with the user, mirroring the tool store
/// pattern (#2232). Only overrides are stored; the effective list layers overrides on top of the
/// built-in defaults from <see cref="NavOrderDefaults"/>.
/// </summary>
public sealed class SqliteNavOrderStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    ILogger<SqliteNavOrderStore>? logger = null) : INavOrderStore
{
    private readonly string _dbPath = dbPath;
    private readonly SqliteWalMaintenance _walMaintenance = new(fileSystem);
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly ILogger<SqliteNavOrderStore> _logger = logger ?? NullLogger<SqliteNavOrderStore>.Instance;
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
                CREATE TABLE IF NOT EXISTS nav_order (
                    nav_key TEXT PRIMARY KEY,
                    sort_order INTEGER NOT NULL
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
    public async Task<IReadOnlyList<NavItemOrder>> ListAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        var overrides = await ReadOverridesAsync(ct).ConfigureAwait(false);

        // Layer stored overrides on top of the built-in defaults so every built-in key always
        // appears exactly once, sorted by its effective order.
        var effective = NavOrderDefaults.Defaults
            .Select(pair => new NavItemOrder(
                pair.Key,
                overrides.TryGetValue(pair.Key, out var stored) ? stored : pair.Value))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return effective;
    }

    /// <inheritdoc />
    public async Task SetOrderAsync(string key, int order, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO nav_order (nav_key, sort_order)
                VALUES ($key, $order)
                ON CONFLICT(nav_key) DO UPDATE SET sort_order = excluded.sort_order
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$order", order);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Set nav order override '{NavKey}' = {Order}.", key, order);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM nav_order WHERE nav_key = $key";
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Reset nav order override '{NavKey}' to default.", key);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<Dictionary<string, int>> ReadOverridesAsync(CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT nav_key, sort_order FROM nav_order";

        Dictionary<string, int> overrides = new(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            overrides[reader.GetString(0)] = reader.GetInt32(1);

        return overrides;
    }

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.Create(_connectionString);
}
