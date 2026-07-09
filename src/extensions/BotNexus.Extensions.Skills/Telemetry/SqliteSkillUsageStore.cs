using System.Globalization;
using System.IO.Abstractions;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Extensions.Skills.Telemetry;

/// <summary>
/// SQLite-backed <see cref="ISkillUsageTelemetry"/> (#1833). Follows the established BotNexus store
/// shape (see <c>SqliteMemoryStore</c> / <c>SqliteExtensionStateStore</c>): a per-operation
/// connection obtained from the shared <see cref="SqliteConnectionFactory"/> (so the standard
/// busy-timeout policy is applied on every open), filesystem-aware journal mode via
/// <see cref="SqliteWalMaintenance"/>, and a write lock serialising the upserts.
/// </summary>
/// <remarks>
/// Counters are upserted with an <c>INSERT ... ON CONFLICT</c> so the first touch of a skill
/// creates the row and subsequent touches increment in place. Recording is best-effort by
/// contract; the store itself does not swallow exceptions (callers on the tool hot path do), so
/// tests can still assert failure behaviour directly against the store.
/// </remarks>
public sealed class SqliteSkillUsageStore : ISkillUsageTelemetry, IAsyncDisposable
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
    public SqliteSkillUsageStore(string dbPath, IFileSystem? fileSystem = null)
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
                CREATE TABLE IF NOT EXISTS skill_usage (
                    skill_name TEXT PRIMARY KEY,
                    view_count INTEGER NOT NULL DEFAULT 0,
                    use_count INTEGER NOT NULL DEFAULT 0,
                    patch_count INTEGER NOT NULL DEFAULT 0,
                    last_used_at TEXT NULL,
                    created_by TEXT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0
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
    public Task RecordViewAsync(string skillName, CancellationToken cancellationToken = default)
        => IncrementAsync(skillName, "view_count", cancellationToken);

    /// <inheritdoc />
    public Task RecordUseAsync(string skillName, CancellationToken cancellationToken = default)
        => IncrementAsync(skillName, "use_count", cancellationToken);

    /// <inheritdoc />
    public Task RecordPatchAsync(string skillName, CancellationToken cancellationToken = default)
        => IncrementAsync(skillName, "patch_count", cancellationToken);

    // Increments a single counter column and refreshes last_used_at, creating the row on first touch.
    // The column name is not user-supplied - it comes from the three fixed call sites above - so the
    // interpolation into SQL is safe; the skill name and timestamp are still bound as parameters.
    private async Task IncrementAsync(string skillName, string column, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO skill_usage (skill_name, {column}, last_used_at)
                VALUES ($name, 1, $now)
                ON CONFLICT(skill_name) DO UPDATE SET
                    {column} = {column} + 1,
                    last_used_at = $now;
                """;
            command.Parameters.AddWithValue("$name", skillName);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RecordCreatedAsync(string skillName, string createdBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // Stamp provenance on first creation; a later create for the same name (after delete +
            // recreate) refreshes created_by without disturbing the accumulated counters.
            command.CommandText = """
                INSERT INTO skill_usage (skill_name, created_by, last_used_at)
                VALUES ($name, $createdBy, $now)
                ON CONFLICT(skill_name) DO UPDATE SET
                    created_by = $createdBy,
                    last_used_at = $now;
                """;
            command.Parameters.AddWithValue("$name", skillName);
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
    public async Task SetPinnedAsync(string skillName, bool pinned, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO skill_usage (skill_name, pinned)
                VALUES ($name, $pinned)
                ON CONFLICT(skill_name) DO UPDATE SET pinned = $pinned;
                """;
            command.Parameters.AddWithValue("$name", skillName);
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillUsageRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT skill_name, view_count, use_count, patch_count, last_used_at, created_by, pinned
            FROM skill_usage
            ORDER BY (last_used_at IS NULL), last_used_at DESC, skill_name ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<SkillUsageRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadRecord(reader));

        return results;
    }

    /// <inheritdoc />
    public async Task<SkillUsageRecord?> GetAsync(string skillName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT skill_name, view_count, use_count, patch_count, last_used_at, created_by, pinned
            FROM skill_usage
            WHERE skill_name = $name;
            """;
        command.Parameters.AddWithValue("$name", skillName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.Create(_connectionString);

    private static SkillUsageRecord ReadRecord(SqliteDataReader reader) => new()
    {
        SkillName = reader.GetString(0),
        ViewCount = reader.GetInt64(1),
        UseCount = reader.GetInt64(2),
        PatchCount = reader.GetInt64(3),
        LastUsedAt = reader.IsDBNull(4)
            ? null
            : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CreatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
        Pinned = !reader.IsDBNull(6) && reader.GetInt64(6) != 0
    };
}
