using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

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

    internal static LegacyToolInvocationBackfillReport RunConnectionString(
        string connectionString,
        int batchSize,
        bool commit)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = commit ? connection.BeginTransaction() : null;
        var rows = ReadBatch(connection, transaction, batchSize);
        var invocationCount = rows.Select(row => (row.SessionId, row.ToolCallId)).Distinct().Count();
        var hasMore = HasMore(connection, transaction, rows.Count == 0 ? null : rows[^1].Id);

        if (!commit)
            return new LegacyToolInvocationBackfillReport(rows.Count, 0, invocationCount, hasMore, false);

        foreach (var group in rows.GroupBy(row => (row.SessionId, row.ToolCallId)))
        {
            UpsertInvocation(connection, transaction!, group);
            LinkRows(connection, transaction!, group);
        }

        transaction!.Commit();
        return new LegacyToolInvocationBackfillReport(rows.Count, rows.Count, invocationCount, hasMore, true);
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

public sealed record LegacyToolInvocationBackfillReport(
    int ScannedRows,
    int LinkedRows,
    int InvocationCount,
    bool HasMore,
    bool Committed);
