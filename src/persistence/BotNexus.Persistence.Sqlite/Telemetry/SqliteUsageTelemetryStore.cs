using System.Globalization;
using System.IO.Abstractions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite.Telemetry;

/// <summary>
/// SQLite-backed <see cref="IUsageTelemetry"/> (#1850). Generalizes the skill-usage store shipped in
/// #1846 into a namespaced primitive: a single database holds all consumers, isolated by a
/// <c>namespace</c> column. Follows the established BotNexus store shape — a per-operation connection
/// obtained from the shared <see cref="SqliteConnectionFactory"/> (#1541) so the standard
/// busy-timeout policy is applied on every open, filesystem-aware journal mode via
/// <see cref="SqliteWalMaintenance"/>, and a write lock serialising the upserts.
/// </summary>
/// <remarks>
/// Metadata (provenance, pin, freshness) lives in <c>usage_entity</c> keyed by
/// <c>(namespace, key)</c>; the arbitrary named counters live in a child <c>usage_counter</c> table
/// keyed by <c>(namespace, key, counter)</c>. This lets consumers use any counter names without a
/// schema change while keeping increments atomic per counter.
/// </remarks>
public sealed class SqliteUsageTelemetryStore : IUsageTelemetry, IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteWalMaintenance _walMaintenance;
    private readonly string _connectionString;
    private readonly IFileSystem _fileSystem;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Creates a store persisting to <paramref name="dbPath"/>. The parent directory is created on
    /// first use. Pass an <see cref="IFileSystem"/> in tests to run against an in-memory filesystem.
    /// </summary>
    public SqliteUsageTelemetryStore(string dbPath, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _fileSystem = fileSystem ?? new FileSystem();
        _walMaintenance = new SqliteWalMaintenance(fileSystem);
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    }

    private async Task InitializeAsync(CancellationToken ct)
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
                CREATE TABLE IF NOT EXISTS usage_entity (
                    namespace TEXT NOT NULL,
                    key TEXT NOT NULL,
                    last_used_at TEXT NULL,
                    created_by TEXT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (namespace, key)
                );
                CREATE TABLE IF NOT EXISTS usage_counter (
                    namespace TEXT NOT NULL,
                    key TEXT NOT NULL,
                    counter TEXT NOT NULL,
                    count INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (namespace, key, counter)
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
    public async Task IncrementAsync(string @namespace, string key, string counterName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(counterName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            await using var entity = connection.CreateCommand();
            entity.CommandText = """
                INSERT INTO usage_entity (namespace, key, last_used_at)
                VALUES ($ns, $key, $now)
                ON CONFLICT(namespace, key) DO UPDATE SET last_used_at = $now;
                """;
            entity.Parameters.AddWithValue("$ns", @namespace);
            entity.Parameters.AddWithValue("$key", key);
            entity.Parameters.AddWithValue("$now", now);
            await entity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var counter = connection.CreateCommand();
            counter.CommandText = """
                INSERT INTO usage_counter (namespace, key, counter, count)
                VALUES ($ns, $key, $counter, 1)
                ON CONFLICT(namespace, key, counter) DO UPDATE SET count = count + 1;
                """;
            counter.Parameters.AddWithValue("$ns", @namespace);
            counter.Parameters.AddWithValue("$key", key);
            counter.Parameters.AddWithValue("$counter", counterName);
            await counter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RecordCreatedAsync(string @namespace, string key, string createdBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // Stamp provenance on first creation; a later create for the same key (after delete +
            // recreate) refreshes created_by without disturbing the accumulated counters.
            command.CommandText = """
                INSERT INTO usage_entity (namespace, key, created_by, last_used_at)
                VALUES ($ns, $key, $createdBy, $now)
                ON CONFLICT(namespace, key) DO UPDATE SET
                    created_by = $createdBy,
                    last_used_at = $now;
                """;
            command.Parameters.AddWithValue("$ns", @namespace);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$createdBy", (object?)createdBy ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetPinnedAsync(string @namespace, string key, bool pinned, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO usage_entity (namespace, key, pinned)
                VALUES ($ns, $key, $pinned)
                ON CONFLICT(namespace, key) DO UPDATE SET pinned = $pinned;
                """;
            command.Parameters.AddWithValue("$ns", @namespace);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageRecord>> GetAllAsync(string @namespace, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT namespace, key, last_used_at, created_by, pinned
            FROM usage_entity
            WHERE namespace = $ns
            ORDER BY (last_used_at IS NULL), last_used_at DESC, key ASC;
            """;
        command.Parameters.AddWithValue("$ns", @namespace);

        var entities = new List<UsageRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                entities.Add(ReadEntity(reader));
        }

        var results = new List<UsageRecord>(entities.Count);
        foreach (var entity in entities)
        {
            var counters = await LoadCountersAsync(connection, entity.Namespace, entity.Key, cancellationToken).ConfigureAwait(false);
            results.Add(entity with { Counters = counters });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<UsageRecord?> GetAsync(string @namespace, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT namespace, key, last_used_at, created_by, pinned
            FROM usage_entity
            WHERE namespace = $ns AND key = $key;
            """;
        command.Parameters.AddWithValue("$ns", @namespace);
        command.Parameters.AddWithValue("$key", key);

        UsageRecord? entity;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            entity = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadEntity(reader)
                : null;
        }

        if (entity is null)
            return null;

        var counters = await LoadCountersAsync(connection, @namespace, key, cancellationToken).ConfigureAwait(false);
        return entity with { Counters = counters };
    }

    // Reads all named counters for a single entity into a dictionary (missing counters imply zero).
    private static async Task<IReadOnlyDictionary<string, long>> LoadCountersAsync(
        SqliteConnection connection, string @namespace, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT counter, count FROM usage_counter
            WHERE namespace = $ns AND key = $key;
            """;
        command.Parameters.AddWithValue("$ns", @namespace);
        command.Parameters.AddWithValue("$key", key);

        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            counters[reader.GetString(0)] = reader.GetInt64(1);

        return counters;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.Create(_connectionString);

    // Reads the metadata columns; counters are populated by the caller in a follow-up query.
    private static UsageRecord ReadEntity(SqliteDataReader reader) => new()
    {
        Namespace = reader.GetString(0),
        Key = reader.GetString(1),
        Counters = new Dictionary<string, long>(StringComparer.Ordinal),
        LastUsedAt = reader.IsDBNull(2)
            ? null
            : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CreatedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
        Pinned = !reader.IsDBNull(4) && reader.GetInt64(4) != 0
    };
}
