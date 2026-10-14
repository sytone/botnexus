using System.Globalization;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions;

public sealed partial class SqliteSessionStore
{
    private static async Task EnsureAgentRunSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_runs (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                agent_run_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT,
                outcome TEXT NOT NULL,
                completed_result_count INTEGER,
                guard_observations_json TEXT NOT NULL,
                UNIQUE(session_id, agent_run_id)
            );
            CREATE INDEX IF NOT EXISTS idx_agent_runs_session_sequence ON agent_runs(session_id, sequence);
            CREATE TRIGGER IF NOT EXISTS delete_session_agent_runs AFTER DELETE ON sessions
            BEGIN DELETE FROM agent_runs WHERE session_id = OLD.id; END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordAgentRunAsync(SessionId sessionId, AgentRunEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.CompletedResultCount < 0) throw new ArgumentOutOfRangeException(nameof(evidence));
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var sessionLock = await AcquireSessionLockAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await RetryOnTransientAsync(async () =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // The single statement fences against deletion/sealing without loading history or resurrecting a session.
            // Only Running can advance; a first terminal remains immutable, including conflicting retries.
            command.CommandText = """
                INSERT INTO agent_runs(session_id, agent_run_id, started_at, completed_at, outcome,
                                       completed_result_count, guard_observations_json)
                SELECT $session, $run, $started, $completed, $outcome, $count, $guards
                WHERE EXISTS(SELECT 1 FROM sessions WHERE id = $session AND COALESCE(status,'Active') NOT IN ('Sealed','Expired'))
                ON CONFLICT(session_id, agent_run_id) DO UPDATE SET
                    completed_at = excluded.completed_at,
                    outcome = excluded.outcome,
                    completed_result_count = excluded.completed_result_count,
                    guard_observations_json = excluded.guard_observations_json
                WHERE agent_runs.outcome = 'Running' AND agent_runs.completed_at IS NULL
                      AND excluded.outcome <> 'Running';
                """;
            var outcome = evidence.Outcome is "Running" or "Completed" or "Parked" or "Cancelled" or "Failed" or "Unknown"
                ? evidence.Outcome : "Unknown";
            var measured = evidence.CompletedAt.HasValue && (outcome is "Completed" or "Parked");
            command.Parameters.AddWithValue("$session", sessionId.Value);
            command.Parameters.AddWithValue("$run", evidence.AgentRunId.Value);
            command.Parameters.AddWithValue("$started", evidence.StartedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$completed", outcome == "Running" ? DBNull.Value :
                evidence.CompletedAt is { } completed ? completed.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            command.Parameters.AddWithValue("$outcome", outcome);
            command.Parameters.AddWithValue("$count", measured && evidence.CompletedResultCount is { } count ? count : DBNull.Value);
            command.Parameters.AddWithValue("$guards", JsonSerializer.Serialize(GuardEvidenceSanitizer.Sanitize(evidence.GuardObservations), JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentRunEvidencePage> QueryAgentRunsAsync(SessionId sessionId, int limit, long afterSequence, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return await RetryOnTransientAsync(async () =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Keep the run page and independent legacy sample on one read snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            var rows = new List<AgentRunEvidenceRow>(limit + 1);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT sequence, agent_run_id, started_at, completed_at, outcome,
                           completed_result_count, guard_observations_json
                    FROM agent_runs WHERE session_id = $session AND sequence > $after
                    ORDER BY sequence ASC LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$session", sessionId.Value);
                command.Parameters.AddWithValue("$after", afterSequence);
                command.Parameters.AddWithValue("$limit", limit + 1);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var guards = JsonSerializer.Deserialize<GuardObservation[]>(reader.GetString(6), JsonOptions) ?? [];
                    rows.Add(new AgentRunEvidenceRow(AgentRunId.From(reader.GetString(1)),
                        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                        reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        GuardEvidenceSanitizer.Sanitize(guards), reader.GetInt64(0)));
                }
            }
            var hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(limit);
            var legacy = 0;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // IDs only: no tool arguments/content, no full COUNT or transcript scan.
                command.CommandText = """
                    SELECT id FROM tool_invocations
                    WHERE session_id = $session AND agent_run_id IS NULL ORDER BY id LIMIT 1001;
                    """;
                command.Parameters.AddWithValue("$session", sessionId.Value);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) legacy++;
            }
            transaction.Commit();
            var semantic = 0;
            var fuse = 0;
            var counts = new List<int>(rows.Count);
            foreach (var row in rows)
            {
                var terminalGuards = row.GuardObservations.Where(g => g.Disposition == "terminal").ToArray();
                var parked = row.Outcome == "Parked" && row.CompletedAt.HasValue;
                if (parked && terminalGuards.Any(g => g.AbsoluteLimitReached)) fuse++;
                if (parked && terminalGuards.Any(g => !g.AbsoluteLimitReached)) semantic++;
                if (row.CompletedAt.HasValue && row.CompletedResultCount is { } count
                    && (row.Outcome == "Completed" || parked && terminalGuards.Length > 0)) counts.Add(count);
            }
            counts.Sort();
            int? Rank(double percentile) => counts.Count == 0 ? null : counts[(int)Math.Ceiling(percentile * counts.Count) - 1];
            return new AgentRunEvidencePage(rows, counts.Count, semantic, fuse, rows.Count - counts.Count,
                Rank(.50), Rank(.95), Rank(.99), Math.Min(legacy, 1000), Math.Min(legacy, 1000), legacy > 1000,
                hasMore, rows.Count == 0 ? afterSequence : rows[^1].Sequence);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
