using System.Globalization;
using System.Text;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;
using BotNexus.Persistence.Sqlite;

namespace BotNexus.Memory;

public sealed class SqliteMemoryStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    MemoryLikeFallbackOptions? likeFallbackOptions = null) : IMemoryStore
{
    private const double DefaultHalfLifeDays = 30d;
    private readonly string _dbPath = dbPath;
    private readonly SqliteWalMaintenance _walMaintenance = new(fileSystem);
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // The LIKE fallback (used only when FTS errors out or the DB is transiently busy)
    // is an unindexable full scan, so it is bounded by a recency window + row ceiling
    // to keep degraded-mode cost finite. The FTS primary path is unaffected.
    private readonly MemoryLikeFallbackOptions _likeFallbackOptions =
        likeFallbackOptions ?? MemoryLikeFallbackOptions.Default;
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

            // #1436: filesystem-aware journal mode (WAL on local disk, DELETE on network
            // mounts) with bounded wal_autocheckpoint, consolidated into the shared helper.
            await _walMaintenance.ApplyJournalModeAsync(connection, _dbPath, cancellationToken: ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS memories (
                    rowid INTEGER PRIMARY KEY AUTOINCREMENT,
                    id TEXT NOT NULL UNIQUE,
                    agent_id TEXT NOT NULL,
                    session_id TEXT NULL,
                    turn_index INTEGER NULL,
                    source_type TEXT NOT NULL,
                    content TEXT NOT NULL,
                    metadata_json TEXT NULL,
                    embedding BLOB NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NULL,
                    expires_at TEXT NULL,
                    is_archived INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS idx_memories_agent_id ON memories(agent_id);
                CREATE INDEX IF NOT EXISTS idx_memories_session_id ON memories(session_id);
                CREATE INDEX IF NOT EXISTS idx_memories_created_at ON memories(created_at);

                CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts
                USING fts5(content, content='memories', content_rowid='rowid');

                CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories BEGIN
                    INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
                END;

                CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content) VALUES('delete', old.rowid, old.content);
                END;

                CREATE TRIGGER IF NOT EXISTS memories_au AFTER UPDATE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content) VALUES('delete', old.rowid, old.content);
                    INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
                END;

                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER NOT NULL
                );

                INSERT INTO schema_version(version)
                SELECT 1
                WHERE NOT EXISTS (SELECT 1 FROM schema_version);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
            var createdAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt;
            var toInsert = entry with { Id = id, CreatedAt = createdAt };

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memories (
                    id, agent_id, session_id, turn_index, source_type, content, metadata_json,
                    embedding, created_at, updated_at, expires_at, is_archived)
                VALUES (
                    $id, $agentId, $sessionId, $turnIndex, $sourceType, $content, $metadataJson,
                    $embedding, $createdAt, $updatedAt, $expiresAt, $isArchived)
                """;
            BindParameters(command, toInsert);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return toInsert;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await InitializeAsync(ct).ConfigureAwait(false);

        return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, agent_id, session_id, turn_index, source_type, content, metadata_json,
                       embedding, created_at, updated_at, expires_at, is_archived
                FROM memories
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false)
                ? ReadMemory(reader)
                : null;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await InitializeAsync(ct).ConfigureAwait(false);

        var cappedLimit = Math.Clamp(limit, 1, int.MaxValue);
        return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, agent_id, session_id, turn_index, source_type, content, metadata_json,
                       embedding, created_at, updated_at, expires_at, is_archived
                FROM memories
                WHERE session_id = $sessionId
                ORDER BY created_at DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$limit", cappedLimit);

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            List<MemoryEntry> results = [];
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                results.Add(ReadMemory(reader));

            return results as IReadOnlyList<MemoryEntry>;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var sanitized = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(sanitized))
            return [];

        var limit = Math.Clamp(topK, 1, 100);
        var lambda = Math.Log(2d) / DefaultHalfLifeDays;
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var sql = new StringBuilder(
                """
                SELECT m.id, m.agent_id, m.session_id, m.turn_index, m.source_type, m.content, m.metadata_json,
                       m.embedding, m.created_at, m.updated_at, m.expires_at, m.is_archived,
                       -bm25(memories_fts) AS bm25_rank,
                       (julianday('now') - julianday(m.created_at)) AS age_days
                FROM memories_fts
                INNER JOIN memories m ON m.rowid = memories_fts.rowid
                WHERE memories_fts MATCH $query
                  AND m.is_archived = 0
                """);

            command.Parameters.AddWithValue("$query", sanitized);

            AppendFilters(sql, command, filter);

            sql.AppendLine("ORDER BY bm25_rank DESC LIMIT $limit");
            command.Parameters.AddWithValue("$limit", limit * 5);
            command.CommandText = sql.ToString();

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            List<(MemoryEntry Entry, double Score)> ranked = [];
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var entry = ReadMemory(reader);
                var bm25Rank = reader.IsDBNull(12) ? 0d : reader.GetDouble(12);
                var ageDays = reader.IsDBNull(13) ? 0d : Math.Max(0d, reader.GetDouble(13));
                var finalScore = bm25Rank * Math.Exp(-lambda * ageDays);
                ranked.Add((entry, finalScore));
            }

            return ranked
                .OrderByDescending(item => item.Score)
                .Take(limit)
                .Select(item => item.Entry)
                .ToList();
        }
        catch (SqliteException ex) when (SqliteRetryHelper.IsTransient(ex))
        {
            // Transient lock/busy — retry the whole search once via LIKE fallback
            return await SearchWithLikeFallbackAsync(sanitized, limit, filter, lambda, ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // FTS syntax or corruption — fall back to LIKE search
            return await SearchWithLikeFallbackAsync(sanitized, limit, filter, lambda, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), MAX(created_at)
                FROM memories
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);

            var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            DateTimeOffset? lastIndexedAt = reader.IsDBNull(1)
                ? null
                : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            var sizeBytes = _fileSystem.File.Exists(_dbPath) ? _fileSystem.FileInfo.New(_dbPath).Length : 0L;
            return new MemoryStoreStats(count, sizeBytes, lastIndexedAt);
        }, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
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

    private static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var sanitized = query
            .Replace("\"", " ", StringComparison.Ordinal)
            .Replace("'", " ", StringComparison.Ordinal)
            .Replace("(", " ", StringComparison.Ordinal)
            .Replace(")", " ", StringComparison.Ordinal)
            .Replace(":", " ", StringComparison.Ordinal)
            .Replace("*", " ", StringComparison.Ordinal)
            .Replace("+", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);

        return string.Join(" ", sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private Task<IReadOnlyList<MemoryEntry>> SearchWithLikeFallbackAsync(
        string sanitizedQuery,
        int limit,
        MemorySearchFilter? filter,
        double lambda,
        CancellationToken ct)
        => SearchWithLikeFallbackAsync(sanitizedQuery, limit, filter, lambda, _likeFallbackOptions, ct);

    /// <summary>
    /// Best-effort LIKE-based search used only when the FTS primary path errors out
    /// (syntax/corruption) or the database is transiently busy. Because
    /// <c>content LIKE '%term%'</c> uses a leading wildcard it cannot use an index and
    /// would otherwise full-scan the entire <c>memories</c> table on a path that is hit
    /// precisely when the store is already degraded. It is therefore bounded by a recency
    /// window (<see cref="MemoryLikeFallbackOptions.RecencyWindowDays"/>) and a hard scan
    /// ceiling (<see cref="MemoryLikeFallbackOptions.MaxScanRows"/>) so degraded-mode cost
    /// stays finite. This makes the fallback non-exhaustive by design; the FTS primary
    /// path is unaffected. The internal overload exists so tests can drive the fallback
    /// directly with a tight window/ceiling.
    /// </summary>
    internal async Task<IReadOnlyList<MemoryEntry>> SearchWithLikeFallbackAsync(
        string sanitizedQuery,
        int limit,
        MemorySearchFilter? filter,
        double lambda,
        MemoryLikeFallbackOptions fallbackOptions,
        CancellationToken ct)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        var terms = sanitizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length == 0)
            return [];

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT m.id, m.agent_id, m.session_id, m.turn_index, m.source_type, m.content, m.metadata_json,
                   m.embedding, m.created_at, m.updated_at, m.expires_at, m.is_archived,
                   (julianday('now') - julianday(m.created_at)) AS age_days
            FROM memories m
            WHERE m.is_archived = 0
            """);

        for (var i = 0; i < terms.Length; i++)
        {
            var parameterName = $"$term{i}";
            sql.AppendLine($"  AND m.content LIKE '%' || {parameterName} || '%'");
            command.Parameters.AddWithValue(parameterName, terms[i]);
        }

        // Bound the unindexable full scan to a recency window so the degraded-mode path
        // cannot drift into an unbounded table scan on a large memories table.
        if (fallbackOptions.RecencyWindowDays is { } windowDays && windowDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-windowDays);
            sql.AppendLine("  AND m.created_at >= $fallbackCutoff");
            command.Parameters.AddWithValue("$fallbackCutoff", cutoff.ToString("O"));
        }

        AppendFilters(sql, command, filter);

        // Hard ceiling on the candidate scan (kept >= the caller's requested slice so
        // ranking still has enough rows to order). The result is non-exhaustive by design.
        var scanCeiling = Math.Max(limit * 5, 1);
        if (fallbackOptions.MaxScanRows is { } maxRows && maxRows > 0)
            scanCeiling = Math.Min(scanCeiling, maxRows);

        sql.AppendLine("ORDER BY m.created_at DESC LIMIT $limit");
        command.Parameters.AddWithValue("$limit", scanCeiling);
        command.CommandText = sql.ToString();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<(MemoryEntry Entry, double Score)> ranked = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = ReadMemory(reader);
            var ageDays = reader.IsDBNull(12) ? 0d : Math.Max(0d, reader.GetDouble(12));
            var textScore = terms.Count(term => entry.Content.Contains(term, StringComparison.OrdinalIgnoreCase));
            var finalScore = textScore * Math.Exp(-lambda * ageDays);
            ranked.Add((entry, finalScore));
        }

        return ranked
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item => item.Entry)
            .ToList();
    }

    /// <summary>
    /// Appends the shared <see cref="MemorySearchFilter"/> predicates (source type,
    /// session, date range, tags) and their parameters to <paramref name="sql"/> /
    /// <paramref name="command"/>. Single source of truth for the filter SQL used by both
    /// the FTS primary path and the LIKE fallback so the two cannot silently diverge.
    /// </summary>
    private static void AppendFilters(StringBuilder sql, SqliteCommand command, MemorySearchFilter? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter?.SourceType))
        {
            sql.AppendLine("  AND m.source_type = $sourceType");
            command.Parameters.AddWithValue("$sourceType", filter.SourceType);
        }

        if (!string.IsNullOrWhiteSpace(filter?.SessionId))
        {
            sql.AppendLine("  AND m.session_id = $sessionId");
            command.Parameters.AddWithValue("$sessionId", filter.SessionId);
        }

        if (filter?.AfterDate is not null)
        {
            sql.AppendLine("  AND m.created_at >= $afterDate");
            command.Parameters.AddWithValue("$afterDate", filter.AfterDate.Value.ToString("O"));
        }

        if (filter?.BeforeDate is not null)
        {
            sql.AppendLine("  AND m.created_at <= $beforeDate");
            command.Parameters.AddWithValue("$beforeDate", filter.BeforeDate.Value.ToString("O"));
        }

        if (filter?.Tags is { Count: > 0 })
        {
            for (var i = 0; i < filter.Tags.Count; i++)
            {
                var parameterName = $"$tag{i}";
                sql.AppendLine("  AND EXISTS (");
                sql.AppendLine("      SELECT 1");
                sql.AppendLine("      FROM json_each(COALESCE(m.metadata_json, '{}'), '$.tags') t");
                sql.AppendLine($"      WHERE t.value = {parameterName}");
                sql.AppendLine("  )");
                command.Parameters.AddWithValue(parameterName, filter.Tags[i]);
            }
        }
    }

    private static void BindParameters(SqliteCommand command, MemoryEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$agentId", entry.AgentId);
        command.Parameters.AddWithValue("$sessionId", (object?)entry.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$turnIndex", (object?)entry.TurnIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceType", entry.SourceType);
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$metadataJson", (object?)entry.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$embedding", (object?)entry.Embedding ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", (object?)entry.UpdatedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$expiresAt", (object?)entry.ExpiresAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$isArchived", entry.IsArchived ? 1 : 0);
    }

    private static MemoryEntry ReadMemory(SqliteDataReader reader)
    {
        return new MemoryEntry
        {
            Id = reader.GetString(0),
            AgentId = reader.GetString(1),
            SessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
            TurnIndex = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            SourceType = reader.GetString(4),
            Content = reader.GetString(5),
            MetadataJson = reader.IsDBNull(6) ? null : reader.GetString(6),
            Embedding = reader.IsDBNull(7) ? null : (byte[])reader[7],
            CreatedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            UpdatedAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            ExpiresAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            IsArchived = !reader.IsDBNull(11) && reader.GetInt32(11) != 0
        };
    }
}
