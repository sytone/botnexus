using System.Globalization;
using System.IO.Abstractions;
using BotNexus.Persistence.Sqlite;
using BotNexus.Persistence.Sqlite.Telemetry;
using Microsoft.Data.Sqlite;

namespace BotNexus.Extensions.Skills.Telemetry;

/// <summary>
/// <see cref="ISkillUsageTelemetry"/> facade over the shared durable usage-telemetry primitive
/// (#1850). Preserves the original constructor and public surface shipped in #1846 so the Skills
/// tools, read API, and existing tests do not churn, while delegating all persistence to the
/// generic <see cref="SqliteUsageTelemetryStore"/> under the fixed namespace <c>"skills"</c>.
/// </summary>
/// <remarks>
/// The three fixed skill counters (<c>view</c>/<c>use</c>/<c>patch</c>) that were dedicated columns
/// in the old <c>skill_usage</c> schema are now arbitrary named counters in the shared store, and
/// the generic <see cref="UsageRecord"/> is projected back into the skill-shaped
/// <see cref="SkillUsageRecord"/> so the <c>/api/skills/telemetry</c> DTO/JSON contract is intact.
/// <para>
/// Data migration (no loss): if a legacy <c>skill_usage</c> table exists in the same database file
/// (the pre-#1850 schema), its rows are imported once into the namespaced tables on first init.
/// </para>
/// </remarks>
public sealed class SqliteSkillUsageStore : ISkillUsageTelemetry, IAsyncDisposable
{
    private const string SkillsNamespace = "skills";
    private const string ViewCounter = "view";
    private const string UseCounter = "use";
    private const string PatchCounter = "patch";

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly IFileSystem _fileSystem;
    private readonly SqliteUsageTelemetryStore _store;
    private readonly SemaphoreSlim _migrateLock = new(1, 1);
    private bool _migrated;

    /// <summary>
    /// Creates a store persisting to <paramref name="dbPath"/>. The parent directory is created on
    /// first use. Pass an <see cref="IFileSystem"/> in tests to run against an in-memory filesystem.
    /// </summary>
    public SqliteSkillUsageStore(string dbPath, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _fileSystem = fileSystem ?? new FileSystem();
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
        _store = new SqliteUsageTelemetryStore(dbPath, fileSystem);
    }

    /// <inheritdoc />
    public async Task RecordViewAsync(string skillName, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _store.IncrementAsync(SkillsNamespace, skillName, ViewCounter, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordUseAsync(string skillName, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _store.IncrementAsync(SkillsNamespace, skillName, UseCounter, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordPatchAsync(string skillName, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _store.IncrementAsync(SkillsNamespace, skillName, PatchCounter, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordCreatedAsync(string skillName, string createdBy, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecordCreatedAsync(SkillsNamespace, skillName, createdBy, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetPinnedAsync(string skillName, bool pinned, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _store.SetPinnedAsync(SkillsNamespace, skillName, pinned, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillUsageRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var records = await _store.GetAllAsync(SkillsNamespace, cancellationToken).ConfigureAwait(false);
        var results = new List<SkillUsageRecord>(records.Count);
        foreach (var record in records)
            results.Add(Project(record));
        return results;
    }

    /// <inheritdoc />
    public async Task<SkillUsageRecord?> GetAsync(string skillName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var record = await _store.GetAsync(SkillsNamespace, skillName, cancellationToken).ConfigureAwait(false);
        return record is null ? null : Project(record);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _migrateLock.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
    }

    // Projects the generic namespaced record back into the skill-shaped record so the endpoint
    // DTO/JSON contract (#1846) is preserved byte-for-byte.
    private static SkillUsageRecord Project(UsageRecord record) => new()
    {
        SkillName = record.Key,
        ViewCount = record.GetCounter(ViewCounter),
        UseCount = record.GetCounter(UseCounter),
        PatchCount = record.GetCounter(PatchCounter),
        LastUsedAt = record.LastUsedAt,
        CreatedBy = record.CreatedBy,
        Pinned = record.Pinned
    };

    // One-time best-effort import of pre-#1850 rows from the legacy `skill_usage` table (if present
    // in the same file) into the namespaced tables, so migrating consumers keep their history.
    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        if (_migrated)
            return;

        await _migrateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_migrated)
                return;

            // Nothing to import if the database file does not exist yet (fresh install).
            if (!_fileSystem.File.Exists(_dbPath))
            {
                _migrated = true;
                return;
            }

            await using var connection = SqliteConnectionFactory.Create(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            if (!await LegacyTableExistsAsync(connection, ct).ConfigureAwait(false))
            {
                _migrated = true;
                return;
            }

            var legacyRows = await ReadLegacyRowsAsync(connection, ct).ConfigureAwait(false);
            _migrated = true;

            foreach (var row in legacyRows)
            {
                if (row.CreatedBy is not null)
                    await _store.RecordCreatedAsync(SkillsNamespace, row.SkillName, row.CreatedBy, ct).ConfigureAwait(false);

                await SeedCounterAsync(row.SkillName, ViewCounter, row.ViewCount, ct).ConfigureAwait(false);
                await SeedCounterAsync(row.SkillName, UseCounter, row.UseCount, ct).ConfigureAwait(false);
                await SeedCounterAsync(row.SkillName, PatchCounter, row.PatchCount, ct).ConfigureAwait(false);

                if (row.Pinned)
                    await _store.SetPinnedAsync(SkillsNamespace, row.SkillName, true, ct).ConfigureAwait(false);
            }

            // Drop the legacy table so the import runs exactly once and cannot double-count.
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE IF EXISTS skill_usage;";
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _migrateLock.Release();
        }
    }

    private async Task SeedCounterAsync(string skillName, string counter, long count, CancellationToken ct)
    {
        for (long i = 0; i < count; i++)
            await _store.IncrementAsync(SkillsNamespace, skillName, counter, ct).ConfigureAwait(false);
    }

    private static async Task<bool> LegacyTableExistsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'skill_usage';";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<List<LegacyRow>> ReadLegacyRowsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT skill_name, view_count, use_count, patch_count, created_by, pinned
            FROM skill_usage;
            """;
        var rows = new List<LegacyRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new LegacyRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetInt64(5) != 0));
        }

        return rows;
    }

    private readonly record struct LegacyRow(
        string SkillName, long ViewCount, long UseCount, long PatchCount, string? CreatedBy, bool Pinned);
}
