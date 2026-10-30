using System.Security.Cryptography;
using System.Text;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions;

/// <summary>Clears redundant legacy payload columns only after normalized evidence proves exact parity.</summary>
public static class LegacyToolPayloadCleanup
{
    internal static (LegacyToolPayloadCleanupReport Report, IReadOnlyList<string> SessionIds) ClearAllInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('session_history') WHERE name='tool_args_from_invocation')";
        var hasMarker = Convert.ToInt32(command.ExecuteScalar()) != 0;
        var marker = hasMarker ? ", tool_args_from_invocation=CASE WHEN h.message_kind='tool-result' AND h.tool_args IS NOT NULL THEN 1 ELSE h.tool_args_from_invocation END" : string.Empty;
        // Arguments on a result are not independent evidence. The linked execution owns them.
        // Result bodies, unlike arguments, are cleared only when SQL proves they are retained.
        // No managed payload hydration, hashing, or second per-row verification pass is needed.
        command.CommandText = """
            CREATE TEMP TABLE legacy_cleanup_rows AS
            SELECT h.id,h.session_id,h.tool_invocation_id,
                   CASE WHEN h.message_kind='tool-result'
                             OR h.message_kind='tool-start' AND h.tool_args IS i.arguments_json
                        THEN 1 ELSE 0 END AS clear_args,
                   CASE WHEN h.message_kind='tool-result' AND h.content IS i.result_content THEN 1 ELSE 0 END AS clear_content,
                   CASE WHEN h.message_kind='tool-result'
                             OR h.message_kind='tool-start' AND h.tool_args IS i.arguments_json
                        THEN COALESCE(length(CAST(h.tool_args AS BLOB)),0) ELSE 0 END
                   + CASE WHEN h.message_kind='tool-result' AND h.content IS i.result_content
                        THEN COALESCE(length(CAST(h.content AS BLOB)),0) ELSE 0 END AS bytes
            FROM session_history h JOIN tool_invocations i ON i.id=h.tool_invocation_id AND i.session_id=h.session_id
            WHERE h.message_kind IN ('tool-start','tool-result')
              AND ((h.tool_args IS NOT NULL AND (h.message_kind='tool-result' OR h.message_kind='tool-start' AND h.tool_args IS i.arguments_json))
                OR (h.message_kind='tool-result' AND h.content IS NOT NULL AND h.content IS i.result_content));
            CREATE UNIQUE INDEX legacy_cleanup_rows_id ON legacy_cleanup_rows(id);
            """;
        command.ExecuteNonQuery();
        command.CommandText = $"""
            UPDATE session_history AS h
            SET tool_args=CASE WHEN c.clear_args=1 THEN NULL ELSE h.tool_args END,
                content=CASE WHEN c.clear_content=1 THEN NULL ELSE h.content END{marker}
            FROM legacy_cleanup_rows c WHERE h.id=c.id;
            """;
        command.ExecuteNonQuery();
        var sessions = new List<string>();
        command.CommandText = "SELECT session_id FROM legacy_cleanup_rows UNION SELECT session_id FROM legacy_tool_rows";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) sessions.Add(reader.GetString(0));
        command.CommandText = "SELECT COUNT(DISTINCT tool_invocation_id),COUNT(*),COALESCE(SUM(bytes),0) FROM legacy_cleanup_rows";
        LegacyToolPayloadCleanupReport report;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            report = new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt64(2),-1,-1,false);
        }
        command.CommandText = "DROP TABLE legacy_cleanup_rows; DROP TABLE legacy_tool_rollup; DROP TABLE legacy_tool_evidence; DROP TABLE legacy_tool_affected; DROP TABLE legacy_tool_rows";
        command.ExecuteNonQuery();
        return (report,sessions);
    }

    internal static LegacyToolPayloadCleanupReport RunConnectionString(string connectionString, int batchSize, long afterInvocationId = 0, Action<string>? invalidateSession = null)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");

        using var connection = SqliteConnectionFactory.Create(connectionString);
        try
        {
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
            var cleaned = 0;
            var changedSessions = new HashSet<string>(StringComparer.Ordinal);
            if (eligible.Length > 0)
            {
                // Discovery stays outside the writer lock. Re-read each bounded candidate in the
                // transaction and compare its full evidence snapshot before changing history.
                using var transaction = connection.BeginTransaction();
                foreach (var candidate in eligible)
                {
                    var current = ReadCandidateWindowCore(connection, 1, candidate.Id - 1, candidate.Id, transaction)
                        .FirstOrDefault(item => item.Id == candidate.Id);
                    if (current is null || current != candidate || !IsEligible(current))
                        continue;

                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.Parameters.AddWithValue("$id", candidate.Id);
                    if (IsPartialDuplicate(current))
                    {
                        update.CommandText = "UPDATE session_history SET tool_args=NULL, tool_args_from_invocation=1 WHERE tool_invocation_id=$id AND message_kind='tool-result' AND tool_args=$arguments AND tool_args_from_invocation=0";
                        update.Parameters.AddWithValue("$arguments", candidate.ArgumentsJson);
                    }
                    else
                    {
                        update.CommandText = "UPDATE session_history SET tool_args_from_invocation=CASE WHEN message_kind='tool-result' AND tool_args IS NOT NULL THEN 1 ELSE tool_args_from_invocation END, tool_args=NULL, content=CASE WHEN message_kind='tool-result' THEN NULL ELSE content END WHERE tool_invocation_id=$id AND message_kind IN ('tool-start','tool-result')";
                    }

                    var changed = update.ExecuteNonQuery();
                    if (changed == 0)
                        continue;
                    cleaned++;
                    rows += changed;
                    bytes += IsPartialDuplicate(current) ? Encoding.UTF8.GetByteCount(candidate.ResultArguments!) : candidate.LegacyBytes;
                    changedSessions.Add(candidate.SessionId);
                }

                transaction.Commit();
                foreach (var sessionId in changedSessions)
                    invalidateSession?.Invoke(sessionId);
            }

            // Progress is a bounded existence probe, not an exact whole-corpus count. Protected and
            // remaining counts were telemetry conveniences whose exact computation caused the defect;
            // -1 explicitly reports "not counted" rather than disguising an estimate as a fact.
            var lastScannedId = candidates.Count == 0 ? afterInvocationId : candidates[^1].Id;
            var hasMore = HasAnotherCandidate(connection, lastScannedId) || HasOrphanedInvocation(connection);
            return new(cleaned, rows, bytes, -1, -1, hasMore, lastScannedId, candidates.Count, orphanedInvocationsDeleted);
        }
        catch (SqliteException ex) when (SqliteCapacityDiagnostics.ObserveFailure(connection, ex))
        {
            throw;
        }
    }

    private static int DeleteOrphanedInvocations(SqliteConnection connection, int limit)
    {
        // The absent-session scan may traverse the corpus. Never hold the writer slot
        // during discovery, and never acquire it for an empty maintenance batch.
        var ids = new List<long>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT i.id FROM tool_invocations i
                LEFT JOIN sessions s ON s.id=i.session_id
                WHERE s.id IS NULL ORDER BY i.id LIMIT $limit
                """;
            select.Parameters.AddWithValue("$limit", limit);
            using var reader = select.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        if (ids.Count == 0)
            return 0;

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Revalidate each selected identity: a session may have appeared since discovery.
        // One id per statement also avoids SQLite's parameter-count limit for large batches.
        command.CommandText = """
            DELETE FROM tool_invocations
            WHERE id=$id AND NOT EXISTS(SELECT 1 FROM sessions s WHERE s.id=tool_invocations.session_id)
            """;
        var idParameter = command.Parameters.AddWithValue("$id", 0L);
        var deleted = 0;
        foreach (var id in ids)
        {
            idParameter.Value = id;
            deleted += command.ExecuteNonQuery();
        }
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

    private static List<Candidate> ReadCandidateWindow(SqliteConnection connection, int limit, long afterInvocationId) =>
        ReadCandidateWindowCore(connection, limit, afterInvocationId, null, null);

    private static List<Candidate> ReadCandidateWindowCore(
        SqliteConnection connection,
        int limit,
        long afterInvocationId,
        long? candidateId,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Materialize IDs only: LIMIT must run before payload-bearing joins/aggregation.
        // CROSS JOIN keeps the bounded window outermost even without planner statistics.
        var identityFilter = candidateId.HasValue ? "i.id=$candidateId" : "i.id > $afterInvocationId";
        command.CommandText = $"""
            WITH candidate_ids AS MATERIALIZED (
                SELECT i.id
                FROM tool_invocations i
                WHERE {identityFilter}
                  AND i.status='success' AND i.is_error=0 AND i.retention_state='hot'
                  AND i.arguments_json IS NOT NULL AND i.result_content IS NOT NULL
                  AND EXISTS(
                      SELECT 1 FROM session_history payload
                      WHERE payload.tool_invocation_id=i.id
                        AND (payload.tool_args IS NOT NULL OR (payload.message_kind='tool-result' AND payload.content IS NOT NULL)))
                ORDER BY i.id
                LIMIT $limit)
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
                   MAX(CASE WHEN h.message_kind='tool-result' THEN h.tool_args END) AS result_args,
                   MAX(CASE WHEN h.message_kind='tool-result' THEN COALESCE(h.tool_args_from_invocation,0) END) AS result_args_from_invocation,
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
            FROM candidate_ids c
            CROSS JOIN tool_invocations i ON i.id=c.id
            CROSS JOIN session_history h ON h.tool_invocation_id=i.id
            GROUP BY i.id
            ORDER BY i.id
            """;
        command.Parameters.AddWithValue("$afterInvocationId", afterInvocationId);
        command.Parameters.AddWithValue("$candidateId", (object?)candidateId ?? DBNull.Value);
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
                reader.IsDBNull(18) ? 0 : reader.GetInt64(18),
                ReadNullable(reader, 19),
                ReadNullable(reader, 20),
                ReadNullable(reader, 21),
                // No result row means unknown evidence, not a successful result flag.
                reader.IsDBNull(22) ? null : reader.GetInt64(22),
                reader.GetInt64(23),
                reader.GetInt64(24) != 0));
        }

        return candidates;
    }

    private static bool IsEligible(Candidate candidate) =>
        IsLegacyDuplicate(candidate) || IsPartialDuplicate(candidate);

    private static bool IsLegacyDuplicate(Candidate candidate) =>
        HasValidNormalizedEvidence(candidate)
        && candidate.StartArguments == candidate.ArgumentsJson
        && candidate.LegacyResultContent == candidate.ResultContent;

    private static bool IsPartialDuplicate(Candidate candidate) =>
        HasValidNormalizedEvidence(candidate)
        && candidate.StartArguments is null
        && candidate.LegacyResultContent is null
        && candidate.ResultArguments == candidate.ArgumentsJson
        && candidate.ResultArgumentsFromInvocation == 0;

    private static bool HasValidNormalizedEvidence(Candidate candidate)
    {
        if (candidate.LinkedCount != 2 || candidate.StartCount != 1 || candidate.ResultCount != 1 || candidate.HasUnlinkedSibling)
            return false;
        if (candidate.StartSessionId != candidate.SessionId || candidate.ResultSessionId != candidate.SessionId)
            return false;
        if (candidate.StartCallId != candidate.ToolCallId || candidate.ResultCallId != candidate.ToolCallId)
            return false;
        if (candidate.StartTimestamp != candidate.StartedAt || candidate.ResultTimestamp != candidate.CompletedAt || candidate.ResultIsError != 0)
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
        string? ResultArguments,
        long ResultArgumentsFromInvocation,
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
