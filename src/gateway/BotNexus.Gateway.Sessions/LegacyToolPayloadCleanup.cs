using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions;

/// <summary>Clears redundant legacy payload columns only after normalized evidence proves exact parity.</summary>
public static class LegacyToolPayloadCleanup
{
    internal static LegacyToolPayloadCleanupReport RunConnectionString(string connectionString, int batchSize)
    {
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        connection.CreateFunction<string?, string?>("sha256", value => value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant());
        using var transaction = connection.BeginTransaction();
        var candidates = ReadEligibleIds(connection, transaction, batchSize);
        long bytes = 0;
        var rows = 0;
        foreach (var id in candidates)
        {
            using var size = connection.CreateCommand();
            size.Transaction = transaction;
            size.CommandText = "SELECT COALESCE(SUM(length(CAST(tool_args AS BLOB))),0)+COALESCE(SUM(CASE WHEN message_kind='tool-result' THEN length(CAST(content AS BLOB)) ELSE 0 END),0) FROM session_history WHERE tool_invocation_id=$id";
            size.Parameters.AddWithValue("$id", id);
            bytes += Convert.ToInt64(size.ExecuteScalar());
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE session_history SET tool_args=NULL, content=CASE WHEN message_kind='tool-result' THEN NULL ELSE content END WHERE tool_invocation_id=$id AND message_kind IN ('tool-start','tool-result')";
            update.Parameters.AddWithValue("$id", id);
            rows += update.ExecuteNonQuery();
        }

        var remainingEligible = CountEligible(connection, transaction);
        var hasMore = remainingEligible != 0;
        using var remainingCommand = connection.CreateCommand();
        remainingCommand.Transaction = transaction;
        remainingCommand.CommandText = "SELECT COUNT(*) FROM tool_invocations i WHERE EXISTS(SELECT 1 FROM session_history h WHERE h.tool_invocation_id=i.id AND (h.tool_args IS NOT NULL OR (h.message_kind='tool-result' AND h.content IS NOT NULL)))";
        var remaining = Convert.ToInt32(remainingCommand.ExecuteScalar());
        transaction.Commit();
        return new(candidates.Count, rows, bytes, remaining - remainingEligible, remaining, hasMore);
    }

    private static int CountEligible(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM ({EligibleQuery})";
        command.Parameters.AddWithValue("$limit", long.MaxValue);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static List<long> ReadEligibleIds(SqliteConnection connection, SqliteTransaction transaction, int limit)
    {
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = EligibleQuery;
        select.Parameters.AddWithValue("$limit", limit);
        using var reader = select.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private const string EligibleQuery = """
            SELECT i.id
            FROM tool_invocations i
            JOIN session_history s ON s.tool_invocation_id=i.id AND s.message_kind='tool-start'
            JOIN session_history r ON r.tool_invocation_id=i.id AND r.message_kind='tool-result'
            WHERE i.status='success' AND i.is_error=0 AND i.retention_state='hot'
              AND i.arguments_json IS NOT NULL AND i.result_content IS NOT NULL
              AND (SELECT COUNT(*) FROM session_history h WHERE h.tool_invocation_id=i.id AND h.message_kind='tool-start')=1
              AND (SELECT COUNT(*) FROM session_history h WHERE h.tool_invocation_id=i.id AND h.message_kind='tool-result')=1
              AND (SELECT COUNT(*) FROM session_history h WHERE h.tool_invocation_id=i.id)=2
              AND NOT EXISTS (
                  SELECT 1 FROM session_history h
                  WHERE h.session_id=i.session_id AND h.tool_call_id=i.tool_call_id
                    AND h.tool_invocation_id IS NULL
                    AND (h.message_kind IN ('tool-start','tool-result') OR (h.message_kind IS NULL AND h.role='tool')))
              AND s.session_id=i.session_id AND r.session_id=i.session_id
              AND s.tool_call_id=i.tool_call_id AND r.tool_call_id=i.tool_call_id
              AND s.tool_args=i.arguments_json AND s.timestamp IS i.started_at
              AND r.content=i.result_content AND r.timestamp IS i.completed_at AND COALESCE(r.tool_is_error,0)=0
              AND i.result_bytes=length(CAST(i.result_content AS BLOB))
              AND i.result_sha256=sha256(i.result_content)
              AND (s.tool_args IS NOT NULL OR r.content IS NOT NULL OR r.tool_args IS NOT NULL)
            ORDER BY i.id LIMIT $limit
            """;
}

public sealed record LegacyToolPayloadCleanupReport(
    int CleanedInvocations,
    int CleanedRows,
    long ClearedBytes,
    int ProtectedInvocations,
    int RemainingInvocations,
    bool HasMore);
