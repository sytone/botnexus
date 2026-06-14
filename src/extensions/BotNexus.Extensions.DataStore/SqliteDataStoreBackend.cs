using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace BotNexus.Extensions.DataStore;

/// <summary>
/// SQLite-backed implementation of <see cref="IDataStoreBackend"/>.
/// Each agent has a dedicated DB at <c>{agentWorkspace}/.store/agent-data.db</c>.
/// </summary>
internal sealed class SqliteDataStoreBackend : IDataStoreBackend
{
    private readonly string _dbPath;
    private readonly long _maxSizeBytes;
    private readonly int _maxQueryRows;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteDataStoreBackend(string dbPath, long maxSizeBytes, int maxQueryRows = 1000)
    {
        _dbPath = dbPath;
        _maxSizeBytes = maxSizeBytes;
        _maxQueryRows = maxQueryRows > 0 ? maxQueryRows : 1000;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<DataStoreResult> IngestAsync(string table, string json, CancellationToken ct = default)
    {
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return DataStoreResult.Fail("'data' must be a JSON array of objects.");

                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
                var rows = doc.RootElement.EnumerateArray().ToList();
                if (rows.Count == 0)
                    return DataStoreResult.Ok("0 rows ingested", 0);

                // Collect all column names across all rows
                var allColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                    foreach (var prop in row.EnumerateObject())
                        allColumns.Add(prop.Name);

                // Ensure table exists with inferred schema
                await EnsureTableAsync(conn, table, rows[0], ct).ConfigureAwait(false);

                // Ensure any new columns from subsequent rows are added
                var existingCols = await GetColumnsAsync(conn, table, ct).ConfigureAwait(false);
                foreach (var col in allColumns)
                {
                    if (!existingCols.Contains(col))
                    {
                        var colType = InferColumnType(col, rows);
                        using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{col}\" {colType}";
                        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                // Insert all rows
                var cols = (await GetColumnsAsync(conn, table, ct).ConfigureAwait(false)).ToList();
                int inserted = 0;
                using var txn = conn.BeginTransaction();
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    await InsertRowAsync(conn, txn, table, row, cols, ct).ConfigureAwait(false);
                    inserted++;
                }
                txn.Commit();

                CheckSizeLimit();
                return DataStoreResult.Ok($"{inserted} row{(inserted == 1 ? "" : "s")} ingested.", inserted);
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (DataStoreSizeLimitException ex) { return DataStoreResult.Fail(ex.Message); }
        catch (Exception ex) { return DataStoreResult.Fail($"Ingest failed: {ex.Message}"); }
    }

    public async Task<DataStoreResult> QueryAsync(string sql, CancellationToken ct = default)
    {
        // Defence-in-depth: even though DataStoreTool guards this, reject multi-statement or
        // non-SELECT SQL here too so a direct backend caller cannot smuggle a trailing write
        // past the read-only contract (SQLite executes every batched statement).
        if (!DataStoreTool.IsSingleSelectStatement(sql))
            return DataStoreResult.Fail(
                "Only a single SELECT statement is permitted in 'query'.");
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                var results = new List<Dictionary<string, object?>>();
                var truncated = false;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (results.Count >= _maxQueryRows)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    results.Add(row);
                }

                var payload = JsonSerializer.Serialize(results);
                if (truncated)
                    payload += $"\n(results truncated to {_maxQueryRows} rows; use LIMIT/OFFSET for pagination)";
                return DataStoreResult.Ok(payload, results.Count);
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return DataStoreResult.Fail($"Query failed: {ex.Message}"); }
    }

    public async Task<DataStoreResult> InsertAsync(string table, string json, CancellationToken ct = default)
    {
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return DataStoreResult.Fail("'data' must be a JSON object.");

                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
                var cols = (await GetColumnsAsync(conn, table, ct).ConfigureAwait(false)).ToList();
                if (cols.Count == 0)
                    return DataStoreResult.Fail($"Table '{table}' does not exist. Use 'ingest' to create it.");

                using var txn = conn.BeginTransaction();
                await InsertRowAsync(conn, txn, table, doc.RootElement, cols, ct).ConfigureAwait(false);
                txn.Commit();

                CheckSizeLimit();
                return DataStoreResult.Ok("inserted", 1);
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (DataStoreSizeLimitException ex) { return DataStoreResult.Fail(ex.Message); }
        catch (Exception ex) { return DataStoreResult.Fail($"Insert failed: {ex.Message}"); }
    }

    public async Task<DataStoreResult> DeleteAsync(string table, string where, CancellationToken ct = default)
    {
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM \"{table}\" WHERE {where}";
                int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return DataStoreResult.Ok($"{affected} row{(affected == 1 ? "" : "s")} deleted.", affected);
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return DataStoreResult.Fail($"Delete failed: {ex.Message}"); }
    }

    public async Task<DataStoreResult> UpdateAsync(string table, string set, string where, CancellationToken ct = default)
    {
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(set);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return DataStoreResult.Fail("'set' must be a JSON object of column=value pairs.");

                var properties = doc.RootElement.EnumerateObject().ToList();
                if (properties.Count == 0)
                    return DataStoreResult.Fail("'set' must contain at least one column=value pair.");

                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);

                // Build parameterized SET clause
                var setClauses = new List<string>();
                using var cmd = conn.CreateCommand();
                for (int i = 0; i < properties.Count; i++)
                {
                    setClauses.Add($"\"{properties[i].Name}\" = @s{i}");
                    cmd.Parameters.AddWithValue($"@s{i}", JsonElementToSqlite(properties[i].Value));
                }

                cmd.CommandText = $"UPDATE \"{table}\" SET {string.Join(", ", setClauses)} WHERE {where}";
                int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return DataStoreResult.Ok($"{affected} row{(affected == 1 ? "" : "s")} updated.", affected);
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex) { return DataStoreResult.Fail($"Update failed: invalid JSON in 'set' — {ex.Message}"); }
        catch (Exception ex) { return DataStoreResult.Fail($"Update failed: {ex.Message}"); }
    }

    public async Task<DataStoreResult> SchemaAsync(string table, CancellationToken ct = default)
    {
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                var sb = new StringBuilder();
                bool any = false;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    sb.AppendLine($"{reader["name"]} {reader["type"]}");
                    any = true;
                }
                if (!any)
                    return DataStoreResult.Fail($"Table '{table}' does not exist.");
                return DataStoreResult.Ok(sb.ToString().TrimEnd());
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return DataStoreResult.Fail($"Schema failed: {ex.Message}"); }
    }

    public async Task<DataStoreResult> TablesAsync(CancellationToken ct = default)
    {
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                var tables = new List<string>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    tables.Add(reader.GetString(0));

                return DataStoreResult.Ok(tables.Count > 0 ? string.Join("\n", tables) : "(no tables)", tables.Count);
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return DataStoreResult.Fail($"Tables failed: {ex.Message}"); }
    }

    public async Task<DataStoreResult> CountAsync(string table, string? where = null, CancellationToken ct = default)
    {
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = string.IsNullOrWhiteSpace(where)
                    ? $"SELECT COUNT(*) FROM \"{table}\""
                    : $"SELECT COUNT(*) FROM \"{table}\" WHERE {where}";
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var count = Convert.ToInt64(result);
                var json = System.Text.Json.JsonSerializer.Serialize(new { table, count });
                return DataStoreResult.Ok(json, (int)count);
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return DataStoreResult.Fail($"Count failed: {ex.Message}"); }
    }
    public async Task<DataStoreResult> DropAsync(string table, CancellationToken ct = default)
    {
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP TABLE IF EXISTS \"{table}\"";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return DataStoreResult.Ok($"Table '{table}' dropped.");
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return DataStoreResult.Fail($"Drop failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
        _lock.Dispose();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is not null) return _connection;

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        await _connection.OpenAsync(ct).ConfigureAwait(false);

        // Enable WAL for better concurrent read performance
        using var wal = _connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return _connection;
    }

    private static async Task EnsureTableAsync(
        SqliteConnection conn, string table, JsonElement sampleRow, CancellationToken ct)
    {
        var cols = new StringBuilder();
        foreach (var prop in sampleRow.EnumerateObject())
        {
            if (cols.Length > 0) cols.Append(", ");
            cols.Append($"\"{prop.Name}\" {InferType(prop.Value)}");
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS \"{table}\" ({cols})";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> GetColumnsAsync(
        SqliteConnection conn, string table, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            cols.Add(reader.GetString(1)); // column 1 = name
        return cols;
    }

    private static async Task InsertRowAsync(
        SqliteConnection conn,
        SqliteTransaction txn,
        string table,
        JsonElement row,
        IReadOnlyList<string> tableCols,
        CancellationToken ct)
    {
        // Only insert properties that exist as columns
        var matching = row.EnumerateObject()
            .Where(p => tableCols.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0) return;

        var colList = string.Join(", ", matching.Select(p => $"\"{p.Name}\""));
        var paramList = string.Join(", ", matching.Select((_, i) => $"@p{i}"));

        using var cmd = conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = $"INSERT INTO \"{table}\" ({colList}) VALUES ({paramList})";

        for (int i = 0; i < matching.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", JsonElementToSqlite(matching[i].Value));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static object JsonElementToSqlite(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.Number when el.TryGetInt64(out var lng) => lng,
        JsonValueKind.Number when el.TryGetDouble(out var dbl) => dbl,
        JsonValueKind.String => el.GetString() ?? (object)DBNull.Value,
        _ => el.GetRawText()
    };

    private static string InferType(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "INTEGER",
        JsonValueKind.Number when el.TryGetInt64(out _) => "INTEGER",
        JsonValueKind.Number => "REAL",
        _ => "TEXT"
    };

    private static string InferColumnType(string colName, IReadOnlyList<JsonElement> rows)
    {
        foreach (var row in rows)
        {
            if (row.TryGetProperty(colName, out var prop))
                return InferType(prop);
        }
        return "TEXT";
    }

    private void CheckSizeLimit()
    {
        if (!File.Exists(_dbPath)) return;
        var size = new FileInfo(_dbPath).Length;
        if (size > _maxSizeBytes)
            throw new DataStoreSizeLimitException(
                $"Data store size {size:N0} bytes exceeds limit of {_maxSizeBytes:N0} bytes.");
    }
}

internal sealed class DataStoreSizeLimitException(string message) : Exception(message);
