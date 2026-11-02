using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using BotNexus.Persistence.Sqlite;

namespace BotNexus.Gateway.Sessions;

public static class LegacyToolInvocationBackfill
{
    public static LegacyToolInvocationBackfillReport Run(string databasePath, int batchSize, bool commit)
    {
        var mode = commit ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode
        }.ToString();
        return RunConnectionString(connectionString, batchSize, commit);
    }

    /// <summary>Atomically normalizes all legacy tool rows and records durable migration completion.</summary>
    public static LegacyToolMigrationReport RunAll(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        return RunAllConnectionString(connectionString);
    }

    internal static LegacyToolInvocationBackfillReport RunConnectionString(
        string connectionString,
        int batchSize,
        bool commit)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");

        List<LegacyRow> rows;
        bool hasMore;
        using (var readConnection = SqliteConnectionFactory.Create(connectionString))
        {
            try
            {
                readConnection.Open();
                rows = ReadBatch(readConnection, transaction: null, batchSize);
                hasMore = HasMore(readConnection, transaction: null, rows.Count == 0 ? null : rows[^1].Id);
            }
            catch (SqliteException ex) when (SqliteCapacityDiagnostics.ObserveFailure(readConnection, ex))
            {
                throw;
            }
        }

        var invocationCount = rows.Select(row => (row.SessionId, row.ToolCallId)).Distinct().Count();
        if (!commit || rows.Count == 0)
            return new LegacyToolInvocationBackfillReport(rows.Count, 0, invocationCount, hasMore, commit);

        using var writeConnection = SqliteConnectionFactory.Create(connectionString);
        try
        {
            writeConnection.Open();
            using var transaction = writeConnection.BeginTransaction();
            foreach (var group in rows.GroupBy(row => (row.SessionId, row.ToolCallId)))
            {
                UpsertInvocation(writeConnection, transaction, group);
                LinkRows(writeConnection, transaction, group);
            }

            transaction.Commit();
            return new LegacyToolInvocationBackfillReport(rows.Count, rows.Count, invocationCount, hasMore, true);
        }
        catch (SqliteException ex) when (SqliteCapacityDiagnostics.ObserveFailure(writeConnection, ex))
        {
            throw;
        }
    }

    internal static LegacyToolMigrationReport RunAllConnectionString(
        string connectionString,
        Action<string>? invalidateSession = null)
    {
        using var connection = SqliteConnectionFactory.Create(connectionString);
        try
        {
            connection.Open();
            connection.CreateFunction<string?, string?>("sha256", value => value is null
                ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant());
            connection.CreateFunction<string, string?, string>("tool_key", (call, run) => run is null ? call
                : "agent-run:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{run.Length}:{run}:{call}"))).ToLowerInvariant());
            using var transaction = connection.BeginTransaction();
            using var state = connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandText = "CREATE TABLE IF NOT EXISTS legacy_tool_bulk_migration (version INTEGER PRIMARY KEY, completed_at TEXT NOT NULL); SELECT EXISTS(SELECT 1 FROM legacy_tool_bulk_migration WHERE version=1)";
            if (Convert.ToInt32(state.ExecuteScalar()) != 0)
            {
                transaction.Commit();
                return new(new(0, 0, 0, false, true), new(0, 0, 0, -1, -1, false));
            }
            var backfill = PopulateAll(connection, transaction);
            var cleanup = LegacyToolPayloadCleanup.ClearAllInTransaction(connection, transaction);
            state.CommandText = "INSERT INTO legacy_tool_bulk_migration(version,completed_at) VALUES(1,strftime('%Y-%m-%dT%H:%M:%fZ','now'))";
            state.ExecuteNonQuery();
            transaction.Commit();
            foreach (var sessionId in cleanup.SessionIds)
                invalidateSession?.Invoke(sessionId);
            return new(backfill, cleanup.Report);
        }
        catch (SqliteException ex) when (SqliteCapacityDiagnostics.ObserveFailure(connection, ex))
        {
            throw;
        }
    }

    private static LegacyToolInvocationBackfillReport PopulateAll(SqliteConnection connection, SqliteTransaction transaction)
    {
        var historyRun = HasColumn(connection, transaction, "session_history", "agent_run_id") ? "h.agent_run_id" : "NULL";
        var hasInvocationRun = HasColumn(connection, transaction, "tool_invocations", "agent_run_id");
        var runColumns = hasInvocationRun ? ", agent_run_id, provider_tool_call_id" : string.Empty;
        var runValues = hasInvocationRun ? ", r.agent_run_id, CASE WHEN r.agent_run_id IS NOT NULL THEN r.tool_call_id END" : string.Empty;
        var incomplete = HasColumn(connection, transaction, "session_history", "tool_is_incomplete") ? "COALESCE(h.tool_is_incomplete,0)" : "0";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Freeze identities once; all payload copies and link updates remain inside the same
        // writer transaction. Include linked siblings only for affected executions: row presence,
        // not nullable timestamps, determines pairing and authoritative start precedence.
        command.CommandText = $"""
            CREATE TEMP TABLE legacy_tool_rows AS
            SELECT h.id, h.session_id, h.tool_call_id, {historyRun} AS agent_run_id,
                   tool_key(h.tool_call_id,{historyRun}) AS execution_key,
                   CASE WHEN h.message_kind='tool-start' OR (h.message_kind IS NULL AND h.role='tool' AND h.tool_args IS NOT NULL) THEN 1 ELSE 0 END AS is_start,
                   {incomplete} AS is_incomplete
            FROM session_history h
            WHERE h.tool_invocation_id IS NULL AND h.tool_call_id IS NOT NULL
              AND (h.message_kind IN ('tool-start','tool-result') OR (h.message_kind IS NULL AND h.role='tool'));
            CREATE UNIQUE INDEX legacy_tool_rows_id ON legacy_tool_rows(id);
            CREATE INDEX legacy_tool_rows_identity ON legacy_tool_rows(session_id,execution_key);
            CREATE TEMP TABLE legacy_tool_affected AS
            SELECT DISTINCT session_id,execution_key FROM legacy_tool_rows;
            CREATE UNIQUE INDEX legacy_tool_affected_identity ON legacy_tool_affected(session_id,execution_key);
            CREATE TEMP TABLE legacy_tool_evidence AS
            SELECT h.id,h.session_id,h.tool_call_id,{historyRun} AS agent_run_id,
                   a.execution_key,
                   CASE WHEN h.message_kind='tool-start' OR (h.message_kind IS NULL AND h.role='tool' AND h.tool_args IS NOT NULL) THEN 1 ELSE 0 END AS is_start,
                   {incomplete} AS is_incomplete
            FROM session_history h JOIN legacy_tool_affected a
              ON a.session_id=h.session_id AND a.execution_key=tool_key(h.tool_call_id,{historyRun})
            WHERE h.tool_call_id IS NOT NULL
              AND (h.message_kind IN ('tool-start','tool-result') OR (h.message_kind IS NULL AND h.role='tool'));
            CREATE UNIQUE INDEX legacy_tool_evidence_id ON legacy_tool_evidence(id);
            CREATE TEMP TABLE legacy_tool_rollup AS
            SELECT session_id, execution_key, MIN(tool_call_id) AS tool_call_id, MIN(agent_run_id) AS agent_run_id,
                   COUNT(*) AS row_count, MIN(CASE WHEN is_start=1 THEN id END) AS start_id,
                   MAX(CASE WHEN is_start=0 THEN id END) AS result_id
            FROM legacy_tool_evidence GROUP BY session_id,execution_key;
            CREATE UNIQUE INDEX legacy_tool_rollup_identity ON legacy_tool_rollup(session_id,execution_key);
            INSERT INTO tool_invocations
                (session_id,tool_call_id,tool_name,arguments_json,started_at,completed_at,status,is_error,
                 result_content,result_bytes,result_sha256{runColumns})
            SELECT r.session_id,r.execution_key,COALESCE(s.tool_name,t.tool_name),
                   CASE WHEN r.start_id IS NOT NULL THEN COALESCE(s.tool_args,si.arguments_json)
                        ELSE COALESCE(t.tool_args,ti.arguments_json) END,
                   s.timestamp,CASE WHEN tr.is_incomplete=1 THEN NULL ELSE t.timestamp END,
                   CASE WHEN tr.is_incomplete=1 OR r.start_id IS NULL THEN 'unknown'
                        WHEN r.result_id IS NULL THEN 'incomplete' WHEN t.tool_is_error=1 THEN 'error' ELSE 'success' END,
                   COALESCE(t.tool_is_error,0),COALESCE(t.content,ti.result_content),
                   COALESCE(length(CAST(COALESCE(t.content,ti.result_content) AS BLOB)),0),sha256(COALESCE(t.content,ti.result_content)){runValues}
            FROM legacy_tool_rollup r
            LEFT JOIN session_history s ON s.id=r.start_id
            LEFT JOIN session_history t ON t.id=r.result_id
            LEFT JOIN tool_invocations si ON si.id=s.tool_invocation_id AND si.session_id=s.session_id
            LEFT JOIN tool_invocations ti ON ti.id=t.tool_invocation_id AND ti.session_id=t.session_id
            LEFT JOIN legacy_tool_evidence tr ON tr.id=r.result_id
            WHERE 1=1
            ON CONFLICT(session_id,tool_call_id) DO UPDATE SET
                tool_name=CASE WHEN EXISTS(SELECT 1 FROM legacy_tool_rollup r WHERE r.session_id=excluded.session_id AND r.execution_key=excluded.tool_call_id AND r.start_id IS NOT NULL)
                    THEN COALESCE(excluded.tool_name,tool_invocations.tool_name)
                    ELSE COALESCE(tool_invocations.tool_name,excluded.tool_name) END,
                arguments_json=CASE WHEN EXISTS(SELECT 1 FROM legacy_tool_rollup r WHERE r.session_id=excluded.session_id AND r.execution_key=excluded.tool_call_id AND r.start_id IS NOT NULL)
                    THEN excluded.arguments_json
                    ELSE COALESCE(tool_invocations.arguments_json,excluded.arguments_json) END,
                started_at=COALESCE(tool_invocations.started_at,excluded.started_at),
                completed_at=COALESCE(tool_invocations.completed_at,excluded.completed_at),
                is_error=CASE WHEN tool_invocations.retention_state='hot' AND tool_invocations.result_content IS NULL THEN excluded.is_error ELSE tool_invocations.is_error END,
                result_bytes=CASE WHEN tool_invocations.retention_state='hot' AND tool_invocations.result_content IS NULL THEN excluded.result_bytes ELSE tool_invocations.result_bytes END,
                result_sha256=CASE WHEN tool_invocations.retention_state='hot' AND tool_invocations.result_content IS NULL THEN excluded.result_sha256 ELSE tool_invocations.result_sha256 END,
                result_content=CASE WHEN tool_invocations.retention_state='hot' THEN COALESCE(tool_invocations.result_content,excluded.result_content)
                                    ELSE tool_invocations.result_content END,
                status=CASE WHEN tool_invocations.retention_state!='hot' THEN tool_invocations.status
                            WHEN EXISTS(SELECT 1 FROM legacy_tool_rollup r
                                         WHERE r.session_id=excluded.session_id AND r.execution_key=excluded.tool_call_id
                                           AND r.result_id IS NOT NULL)
                            THEN CASE WHEN excluded.status='unknown' THEN 'unknown'
                                      WHEN tool_invocations.result_content IS NOT NULL
                                      THEN CASE WHEN tool_invocations.is_error=1 THEN 'error' ELSE 'success' END
                                      ELSE excluded.status END
                            WHEN tool_invocations.result_content IS NULL THEN excluded.status
                            ELSE tool_invocations.status END;
            UPDATE session_history AS h
            SET tool_invocation_id=i.id,
                message_kind=CASE WHEN r.is_start=1 THEN 'tool-start' ELSE 'tool-result' END
            FROM legacy_tool_rows r JOIN tool_invocations i ON i.session_id=r.session_id AND i.tool_call_id=r.execution_key
            WHERE h.id=r.id;
            SELECT (SELECT COUNT(*) FROM legacy_tool_rows),(SELECT COUNT(*) FROM legacy_tool_rollup);
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt32(0),reader.GetInt32(0),reader.GetInt32(1),false,true);
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info($table) WHERE name=$column)";
        command.Parameters.AddWithValue("$table",table);
        command.Parameters.AddWithValue("$column",column);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static string LegacyCorrelationPredicate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_history') WHERE name='agent_run_id'";
        return Convert.ToInt64(command.ExecuteScalar()) == 0 ? "1=1" : "agent_run_id IS NULL";
    }

    private static List<LegacyRow> ReadBatch(SqliteConnection connection, SqliteTransaction? transaction, int batchSize)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id, session_id, tool_call_id, tool_name, tool_args, content, timestamp,
                   tool_is_error, message_kind, role
            FROM session_history
            WHERE tool_invocation_id IS NULL
              AND tool_call_id IS NOT NULL
              AND ({LegacyCorrelationPredicate(connection, transaction)})
              AND ({ToolRowPredicate})
            ORDER BY id
            LIMIT $batchSize
            """;
        command.Parameters.AddWithValue("$batchSize", batchSize);
        using var reader = command.ExecuteReader();
        var rows = new List<LegacyRow>();
        while (reader.Read())
        {
            rows.Add(new LegacyRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
                IsStart(reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(4))));
        }
        return rows;
    }

    private static bool HasMore(SqliteConnection connection, SqliteTransaction? transaction, long? lastSelectedId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT EXISTS(
                SELECT 1 FROM session_history
                WHERE tool_invocation_id IS NULL
                  AND tool_call_id IS NOT NULL
              AND ({LegacyCorrelationPredicate(connection, transaction)})
                  AND ({ToolRowPredicate})
                  AND ($lastId IS NULL OR id > $lastId))
            """;
        command.Parameters.AddWithValue("$lastId", (object?)lastSelectedId ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static void UpsertInvocation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<LegacyRow> source)
    {
        var rows = source.OrderBy(row => row.Id).ToList();
        var start = rows.FirstOrDefault(row => row.IsStart);
        var result = rows.LastOrDefault(row => !row.IsStart);
        var fallback = result ?? start!;
        var resultBytes = result?.Content is null ? 0 : Encoding.UTF8.GetByteCount(result.Content);
        var resultSha256 = result?.Content is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Content))).ToLowerInvariant();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tool_invocations
                (session_id, tool_call_id, tool_name, arguments_json, started_at, completed_at,
                 status, is_error, result_content, result_bytes, result_sha256)
            VALUES
                ($sessionId, $toolCallId, $toolName, $arguments, $startedAt, $completedAt,
                 CASE WHEN $hasStart = 0 THEN 'unknown'
                      WHEN $hasResult = 0 THEN 'incomplete'
                      WHEN $isError = 1 THEN 'error' ELSE 'success' END,
                 $isError, $resultContent, $resultBytes, $resultSha256)
            ON CONFLICT(session_id, tool_call_id) DO UPDATE SET
                tool_name = CASE WHEN $hasStart = 1
                                 THEN COALESCE(excluded.tool_name, tool_invocations.tool_name)
                                 ELSE COALESCE(tool_invocations.tool_name, excluded.tool_name) END,
                arguments_json = CASE WHEN $hasStart = 1
                                      THEN COALESCE(excluded.arguments_json, tool_invocations.arguments_json)
                                      ELSE COALESCE(tool_invocations.arguments_json, excluded.arguments_json) END,
                started_at = COALESCE(tool_invocations.started_at, excluded.started_at),
                completed_at = COALESCE(excluded.completed_at, tool_invocations.completed_at),
                is_error = CASE WHEN $hasResult = 1 THEN excluded.is_error ELSE tool_invocations.is_error END,
                result_content = CASE WHEN $hasResult = 1 THEN excluded.result_content ELSE tool_invocations.result_content END,
                result_bytes = CASE WHEN $hasResult = 1 THEN excluded.result_bytes ELSE tool_invocations.result_bytes END,
                result_sha256 = CASE WHEN $hasResult = 1 THEN excluded.result_sha256 ELSE tool_invocations.result_sha256 END,
                status = CASE
                    WHEN $hasStart = 1 AND $hasResult = 1
                        THEN CASE WHEN excluded.is_error = 1 THEN 'error' ELSE 'success' END
                    WHEN $hasStart = 1 AND tool_invocations.status = 'unknown'
                        THEN CASE WHEN tool_invocations.is_error = 1 THEN 'error' ELSE 'success' END
                    WHEN $hasResult = 1 AND tool_invocations.status = 'incomplete'
                        THEN CASE WHEN excluded.is_error = 1 THEN 'error' ELSE 'success' END
                    WHEN $hasResult = 1 AND tool_invocations.status IN ('success', 'error')
                        THEN CASE WHEN excluded.is_error = 1 THEN 'error' ELSE 'success' END
                    ELSE tool_invocations.status END
            """;
        command.Parameters.AddWithValue("$sessionId", fallback.SessionId);
        command.Parameters.AddWithValue("$toolCallId", fallback.ToolCallId);
        command.Parameters.AddWithValue("$toolName", (object?)(start?.ToolName ?? result?.ToolName) ?? DBNull.Value);
        command.Parameters.AddWithValue("$arguments", (object?)(start?.Arguments ?? result?.Arguments) ?? DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", (object?)start?.Timestamp ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", (object?)result?.Timestamp ?? DBNull.Value);
        command.Parameters.AddWithValue("$hasStart", start is null ? 0 : 1);
        command.Parameters.AddWithValue("$hasResult", result is null ? 0 : 1);
        command.Parameters.AddWithValue("$isError", result?.IsError == true ? 1 : 0);
        command.Parameters.AddWithValue("$resultContent", (object?)result?.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("$resultBytes", resultBytes);
        command.Parameters.AddWithValue("$resultSha256", (object?)resultSha256 ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void LinkRows(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<LegacyRow> rows)
    {
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE session_history
                SET tool_invocation_id = (
                    SELECT id FROM tool_invocations
                    WHERE session_id = $sessionId AND tool_call_id = $toolCallId)
                WHERE id = $id AND tool_invocation_id IS NULL
                """;
            command.Parameters.AddWithValue("$sessionId", row.SessionId);
            command.Parameters.AddWithValue("$toolCallId", row.ToolCallId);
            command.Parameters.AddWithValue("$id", row.Id);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Legacy session history row {row.Id} could not be linked.");
        }
    }

    private static bool IsStart(string? kind, string? role, bool argumentsAreNull) =>
        string.Equals(kind, "tool-start", StringComparison.OrdinalIgnoreCase) ||
        (kind is null && string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase) && !argumentsAreNull);

    private const string ToolRowPredicate = """
        message_kind IN ('tool-start', 'tool-result')
        OR (message_kind IS NULL AND role = 'tool')
        """;

    private sealed record LegacyRow(
        long Id,
        string SessionId,
        string ToolCallId,
        string? ToolName,
        string? Arguments,
        string? Content,
        string? Timestamp,
        bool IsError,
        bool IsStart);
}

/// <summary>Committed population and duplicate-cleanup counts for one atomic historical migration.</summary>
public sealed record LegacyToolMigrationReport(
    LegacyToolInvocationBackfillReport Backfill,
    LegacyToolPayloadCleanupReport Cleanup);

public sealed record LegacyToolInvocationBackfillReport(
    int ScannedRows,
    int LinkedRows,
    int InvocationCount,
    bool HasMore,
    bool Committed);
