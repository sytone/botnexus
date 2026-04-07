using System.Globalization;
using System.Text;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory;

public sealed class SqliteMemoryStore(string dbPath) : IMemoryStore
{
    private const double DefaultHalfLifeDays = 30d;
    private readonly string _dbPath = dbPath;
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
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

            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;

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

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, agent_id, session_id, turn_index, source_type, content, metadata_json,
                   embedding, created_at, updated_at, expires_at, is_archived
            FROM memories
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadMemory(reader)
            : null;
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await InitializeAsync(ct).ConfigureAwait(false);

        var cappedLimit = Math.Clamp(limit, 1, int.MaxValue);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
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

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<MemoryEntry> results = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadMemory(reader));

        return results;
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
        catch (SqliteException)
        {
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

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), MAX(created_at)
            FROM memories
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);

        var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        DateTimeOffset? lastIndexedAt = reader.IsDBNull(1)
            ? null
            : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
        var sizeBytes = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0L;
        return new MemoryStoreStats(count, sizeBytes, lastIndexedAt);
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

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

    private async Task<IReadOnlyList<MemoryEntry>> SearchWithLikeFallbackAsync(
        string sanitizedQuery,
        int limit,
        MemorySearchFilter? filter,
        double lambda,
        CancellationToken ct)
    {
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

        sql.AppendLine("ORDER BY m.created_at DESC LIMIT $limit");
        command.Parameters.AddWithValue("$limit", limit * 5);
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
