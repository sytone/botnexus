using System.Security.Cryptography;
using System.Text;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions;

/// <summary>Clears redundant legacy payload columns only after normalized evidence proves exact parity.</summary>
public static class LegacyToolPayloadCleanup
{
    internal static LegacyToolPayloadCleanupReport RunConnectionString(string connectionString, int batchSize, long afterInvocationId = 0)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");

        using var connection = SqliteConnectionFactory.Create(connectionString);
        connection.Open();

        var orphanedInvocationsDeleted = DeleteOrphanedInvocations(connection, batchSize);

        // Candidate selection is deliberately read-only and bounded. The old query registered a
        // managed sha256 SQLite function and reran it across the complete corpus while a write
        // transaction was held after every 100-row batch. Read only the next bounded window here;
        // parity hashing below is then proportional to batchSize, never database size.
        var candidates = ReadCandidateWindow(connection, batchSize, afterInvocationId);
        var eligible = candidates.Where(IsEligible).ToArray();

        long bytes = 0;
        var rows = 0;
        if (eligible.Length > 0)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var candidate in eligible)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE session_history SET tool_args=NULL, content=CASE WHEN message_kind='tool-result' THEN NULL ELSE content END WHERE tool_invocation_id=$id AND message_kind IN ('tool-start','tool-result')";
                update.Parameters.AddWithValue("$id", candidate.Id);
                rows += update.ExecuteNonQuery();
                bytes += candidate.LegacyBytes;
            }

            transaction.Commit();
        }

        // Progress is a bounded existence probe, not an exact whole-corpus count. Protected and
        // remaining counts were telemetry conveniences whose exact computation caused the defect;
        // -1 explicitly reports "not counted" rather than disguising an estimate as a fact.
        var lastScannedId = candidates.Count == 0 ? afterInvocationId : candidates[^1].Id;
        var hasMore = HasAnotherCandidate(connection, lastScannedId) || HasOrphanedInvocation(connection);
        return new(eligible.Length, rows, bytes, -1, -1, hasMore, lastScannedId, candidates.Count, orphanedInvocationsDeleted);
    }

    private static int DeleteOrphanedInvocations(SqliteConnection connection, int limit)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM tool_invocations
            WHERE id IN (
                SELECT i.id
                FROM tool_invocations i
                LEFT JOIN sessions s ON s.id=i.session_id
                WHERE s.id IS NULL
                ORDER BY i.id
                LIMIT $limit)
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var deleted = command.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }

    private static bool HasOrphanedInvocation(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM tool_invocations i
                LEFT JOIN sessions s ON s.id=i.session_id
                WHERE s.id IS NULL
                LIMIT 1)
            """;
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static List<Candidate> ReadCandidateWindow(SqliteConnection connection, int limit, long afterInvocationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.session_id, COALESCE(i.provider_tool_call_id, i.tool_call_id), i.arguments_json, i.started_at,
                   i.completed_at, i.result_content, i.result_bytes, i.result_sha256,
                   COUNT(h.id) AS linked_count,
                   SUM(CASE WHEN h.message_kind='tool-start' THEN 1 ELSE 0 END) AS start_count,
                   SUM(CASE WHEN h.message_kind='tool-result' THEN 1 ELSE 0 END) AS result_count,
                   MAX(CASE WHEN h.message_kind='tool-start' THEN h.session_id END) AS start_session_id,
                   MAX(CASE WHEN h.message_kind='tool-result' THEN h.session_id END) AS result_session_id,
                   MAX(CASE WHEN h.message_kind='tool-start' THEN h.tool_call_id END) AS start_call_id,
                   MAX(CASE WHEN h.message_kind='tool-result' THEN h.tool_call_id END) AS result_call_id,
                   MAX(CASE WHEN h.message_kind='tool-start' THEN h.tool_args END) AS start_args,
                   MAX(CASE WHEN h.message_kind='tool-start' THEN h.timestamp END) AS start_timestamp,
                   MAX(CASE WHEN h.message_kind='tool-result' THEN h.content END) AS result_content,
                   MAX(CASE WHEN h.message_kind='tool-result' THEN h.timestamp END) AS result_timestamp,
                   MAX(CASE WHEN h.message_kind='tool-result' THEN COALESCE(h.tool_is_error,0) END) AS result_is_error,
                   COALESCE(SUM(length(CAST(h.tool_args AS BLOB))),0)
                     + COALESCE(SUM(CASE WHEN h.message_kind='tool-result' THEN length(CAST(h.content AS BLOB)) ELSE 0 END),0) AS legacy_bytes,
                   EXISTS(
                       SELECT 1 FROM session_history u
                       WHERE u.session_id=i.session_id AND u.tool_call_id=COALESCE(i.provider_tool_call_id, i.tool_call_id)
                         AND u.agent_run_id IS i.agent_run_id
                         AND u.tool_invocation_id IS NULL
                         AND (u.message_kind IN ('tool-start','tool-result') OR (u.message_kind IS NULL AND u.role='tool'))
                   ) AS has_unlinked_sibling
            FROM tool_invocations i
            JOIN session_history h ON h.tool_invocation_id=i.id
            WHERE i.id > $afterInvocationId
              AND i.status='success' AND i.is_error=0 AND i.retention_state='hot'
              AND i.arguments_json IS NOT NULL AND i.result_content IS NOT NULL
              AND EXISTS(
                  SELECT 1 FROM session_history payload
                  WHERE payload.tool_invocation_id=i.id
                    AND (payload.tool_args IS NOT NULL OR (payload.message_kind='tool-result' AND payload.content IS NOT NULL)))
            GROUP BY i.id
            ORDER BY i.id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$afterInvocationId", afterInvocationId);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var candidates = new List<Candidate>();
        while (reader.Read())
        {
            candidates.Add(new Candidate(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadNullable(reader, 4),
                ReadNullable(reader, 5),
                reader.GetString(6),
                reader.GetInt64(7),
                ReadNullable(reader, 8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                ReadNullable(reader, 12),
                ReadNullable(reader, 13),
                ReadNullable(reader, 14),
                ReadNullable(reader, 15),
                ReadNullable(reader, 16),
                ReadNullable(reader, 17),
                ReadNullable(reader, 18),
                ReadNullable(reader, 19),
                // No result row means unknown evidence, not a successful result flag.
                reader.IsDBNull(20) ? null : reader.GetInt64(20),
                reader.GetInt64(21),
                reader.GetInt64(22) != 0));
        }

        return candidates;
    }

    private static bool IsEligible(Candidate candidate)
    {
        if (candidate.LinkedCount != 2 || candidate.StartCount != 1 || candidate.ResultCount != 1 || candidate.HasUnlinkedSibling)
            return false;
        if (candidate.StartSessionId != candidate.SessionId || candidate.ResultSessionId != candidate.SessionId)
            return false;
        if (candidate.StartCallId != candidate.ToolCallId || candidate.ResultCallId != candidate.ToolCallId)
            return false;
        if (candidate.StartArguments != candidate.ArgumentsJson || candidate.StartTimestamp != candidate.StartedAt)
            return false;
        if (candidate.LegacyResultContent != candidate.ResultContent || candidate.ResultTimestamp != candidate.CompletedAt || candidate.ResultIsError != 0)
            return false;

        var resultBytes = Encoding.UTF8.GetByteCount(candidate.ResultContent);
        var resultSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.ResultContent))).ToLowerInvariant();
        return candidate.ResultBytes == resultBytes && candidate.ResultSha256 == resultSha;
    }

    private static bool HasAnotherCandidate(SqliteConnection connection, long? lastCandidateId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM tool_invocations i
                WHERE i.id > $lastId
                  AND i.status='success' AND i.is_error=0 AND i.retention_state='hot'
                  AND i.arguments_json IS NOT NULL AND i.result_content IS NOT NULL
                  AND EXISTS(
                      SELECT 1 FROM session_history h
                      WHERE h.tool_invocation_id=i.id
                        AND (h.tool_args IS NOT NULL OR (h.message_kind='tool-result' AND h.content IS NOT NULL)))
                LIMIT 1)
            """;
        command.Parameters.AddWithValue("$lastId", lastCandidateId ?? 0);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static string? ReadNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record Candidate(
        long Id,
        string SessionId,
        string ToolCallId,
        string ArgumentsJson,
        string? StartedAt,
        string? CompletedAt,
        string ResultContent,
        long ResultBytes,
        string? ResultSha256,
        long LinkedCount,
        long StartCount,
        long ResultCount,
        string? StartSessionId,
        string? ResultSessionId,
        string? StartCallId,
        string? ResultCallId,
        string? StartArguments,
        string? StartTimestamp,
        string? LegacyResultContent,
        string? ResultTimestamp,
        long? ResultIsError,
        long LegacyBytes,
        bool HasUnlinkedSibling);
}

public sealed record LegacyToolPayloadCleanupReport(
    int CleanedInvocations,
    int CleanedRows,
    long ClearedBytes,
    int ProtectedInvocations,
    int RemainingInvocations,
    bool HasMore,
    long LastScannedInvocationId = 0,
    int ScannedInvocations = 0,
    int OrphanedInvocationsDeleted = 0);
