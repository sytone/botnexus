using System.Security.Cryptography;
using System.Text;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests;

public sealed class LegacyToolInvocationBackfillTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"botnexus-backfill-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public LegacyToolInvocationBackfillTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "sessions.db");
        CreateCorpus();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Run_PreviewDoesNotMutateAndReportsTheOldestBoundedBatch()
    {
        var before = File.ReadAllBytes(_databasePath);
        var report = LegacyToolInvocationBackfill.Run(_databasePath, batchSize: 2, commit: false);
        report.ScannedRows.ShouldBe(2);
        report.LinkedRows.ShouldBe(0);
        report.InvocationCount.ShouldBe(1);
        report.HasMore.ShouldBeTrue();
        report.Committed.ShouldBeFalse();
        File.ReadAllBytes(_databasePath).ShouldBe(before);
    }

    [Fact]
    public void Run_CommitConvergesInOldestBatchesWithoutChangingTranscriptRows()
    {
        var transcriptBefore = ReadTranscript();
        var first = LegacyToolInvocationBackfill.Run(_databasePath, batchSize: 2, commit: true);
        var second = LegacyToolInvocationBackfill.Run(_databasePath, batchSize: 10, commit: true);
        var repeated = LegacyToolInvocationBackfill.Run(_databasePath, batchSize: 10, commit: true);
        first.ShouldBe(new LegacyToolInvocationBackfillReport(2, 2, 1, true, true));
        second.ShouldBe(new LegacyToolInvocationBackfillReport(5, 5, 4, false, true));
        repeated.ShouldBe(new LegacyToolInvocationBackfillReport(0, 0, 0, false, true));
        Count("tool_invocations").ShouldBe(5);
        Count("session_history WHERE tool_invocation_id IS NOT NULL").ShouldBe(7);
        ReadTranscript().ShouldBe(transcriptBefore);
    }

    [Fact]
    public void Run_CommitPairsRowsAndNormalizesArgumentsStatusesAndResultEvidence()
    {
        const string result = "r\u00e9sultat";
        _ = LegacyToolInvocationBackfill.Run(_databasePath, batchSize: 20, commit: true);
        var complete = ReadInvocation("s1", "complete");
        complete.Status.ShouldBe("success");
        complete.Arguments.ShouldBe("{\"authority\":\"start\"}");
        complete.ResultContent.ShouldBe(result);
        complete.ResultBytes.ShouldBe(Encoding.UTF8.GetByteCount(result));
        complete.ResultSha256.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result))).ToLowerInvariant());
        ReadInvocation("s1", "failed").Status.ShouldBe("error");
        ReadInvocation("s1", "start-only").Status.ShouldBe("incomplete");
        var resultOnly = ReadInvocation("s1", "result-only");
        resultOnly.Status.ShouldBe("unknown");
        resultOnly.Arguments.ShouldBe("{\"fallback\":true}");
    }

    [Fact]
    public void Run_CommitPreservesNullTimestampEvidenceAcrossSplitBatches()
    {
        _ = LegacyToolInvocationBackfill.Run(_databasePath, batchSize: 20, commit: true);
        InsertNullTimestampSplitBatchCorpus();

        for (var batch = 0; batch < 4; batch++)
            _ = LegacyToolInvocationBackfill.Run(_databasePath, batchSize: 1, commit: true);

        var startThenResult = ReadInvocation("null-times", "start-then-result");
        startThenResult.ToolName.ShouldBe("authoritative-start");
        startThenResult.Arguments.ShouldBe("{\"authority\":\"start\"}");
        startThenResult.StartedAt.ShouldBeNull();
        startThenResult.CompletedAt.ShouldBeNull();
        startThenResult.Status.ShouldBe("error");
        startThenResult.IsError.ShouldBeTrue();
        startThenResult.ResultContent.ShouldBe("failed result");
        startThenResult.ResultBytes.ShouldBe(Encoding.UTF8.GetByteCount("failed result"));
        startThenResult.ResultSha256.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("failed result"))).ToLowerInvariant());

        var resultThenStart = ReadInvocation("null-times", "result-then-start");
        resultThenStart.ToolName.ShouldBe("authoritative-late-start");
        resultThenStart.Arguments.ShouldBe("{\"authority\":\"late-start\"}");
        resultThenStart.StartedAt.ShouldBeNull();
        resultThenStart.CompletedAt.ShouldBeNull();
        resultThenStart.Status.ShouldBe("success");
        resultThenStart.IsError.ShouldBeFalse();
        resultThenStart.ResultContent.ShouldBe("successful result");
        resultThenStart.ResultBytes.ShouldBe(Encoding.UTF8.GetByteCount("successful result"));
        resultThenStart.ResultSha256.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("successful result"))).ToLowerInvariant());
    }

    [Fact]
    public void Run_RejectsNonPositiveBatchSize()
    {
        Action act = () => LegacyToolInvocationBackfill.Run(_databasePath, batchSize: 0, commit: false);
        Should.Throw<ArgumentOutOfRangeException>(act).Message.ShouldContain("positive");
    }

    private void CreateCorpus()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE session_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT, content TEXT,
                timestamp TEXT, tool_name TEXT, tool_call_id TEXT, tool_args TEXT,
                tool_is_error INTEGER NOT NULL DEFAULT 0, message_kind TEXT, tool_invocation_id INTEGER);
            CREATE TABLE tool_invocations (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, tool_call_id TEXT NOT NULL,
                tool_name TEXT, arguments_json TEXT, started_at TEXT, completed_at TEXT,
                status TEXT NOT NULL CHECK (status IN ('incomplete', 'success', 'error', 'unknown')),
                is_error INTEGER NOT NULL DEFAULT 0, result_content TEXT, result_bytes INTEGER NOT NULL DEFAULT 0,
                result_sha256 TEXT, retention_state TEXT NOT NULL DEFAULT 'hot', UNIQUE(session_id, tool_call_id));
            INSERT INTO session_history (session_id, role, content, timestamp, tool_name, tool_call_id, tool_args, tool_is_error, message_kind) VALUES
              ('s1', 'assistant', 'start', '2026-01-01T00:00:00Z', 'read', 'complete', '{"authority":"start"}', 0, 'tool-start'),
              ('s1', 'tool', 'r' || char(233) || 'sultat', '2026-01-01T00:00:01Z', 'read-result', 'complete', '{"fallback":false}', 0, 'tool-result'),
              ('s1', 'assistant', 'start', '2026-01-01T00:00:02Z', 'write', 'failed', '{}', 0, 'tool-start'),
              ('s1', 'tool', 'boom', '2026-01-01T00:00:03Z', 'write', 'failed', NULL, 1, 'tool-result'),
              ('s1', 'assistant', 'start', '2026-01-01T00:00:04Z', 'read', 'start-only', '{"x":1}', 0, 'tool-start'),
              ('s1', 'tool', 'orphan result', '2026-01-01T00:00:05Z', 'read', 'result-only', '{"fallback":true}', 0, 'tool-result'),
              ('s2', 'tool', 'other session', '2026-01-01T00:00:06Z', 'read', 'complete', NULL, 0, 'tool-result'),
              ('s1', 'assistant', 'ordinary', '2026-01-01T00:00:07Z', NULL, NULL, NULL, 0, 'message');
            """;
        command.ExecuteNonQuery();
    }

    private void InsertNullTimestampSplitBatchCorpus()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_history (session_id, role, content, timestamp, tool_name, tool_call_id, tool_args, tool_is_error, message_kind) VALUES
              ('null-times', 'assistant', 'start', NULL, 'authoritative-start', 'start-then-result', '{"authority":"start"}', 0, 'tool-start'),
              ('null-times', 'tool', 'failed result', NULL, 'result-name', 'start-then-result', '{"authority":"result"}', 1, 'tool-result'),
              ('null-times', 'tool', 'successful result', NULL, 'result-name', 'result-then-start', '{"authority":"result"}', 0, 'tool-result'),
              ('null-times', 'assistant', 'start', NULL, 'authoritative-late-start', 'result-then-start', '{"authority":"late-start"}', 0, 'tool-start');
            """;
        command.ExecuteNonQuery();
    }

    private int Count(string source)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {source}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private List<(long Id, string? Content, string? Timestamp)> ReadTranscript()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, content, timestamp FROM session_history ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<(long, string?, string?)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    private Invocation ReadInvocation(string sessionId, string toolCallId)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tool_name, arguments_json, started_at, completed_at, status, is_error, result_content, result_bytes, result_sha256 FROM tool_invocations WHERE session_id = @session AND tool_call_id = @call";
        command.Parameters.AddWithValue("@session", sessionId);
        command.Parameters.AddWithValue("@call", toolCallId);
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        return new Invocation(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5) != 0,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private sealed record Invocation(
        string? ToolName,
        string? Arguments,
        string? StartedAt,
        string? CompletedAt,
        string Status,
        bool IsError,
        string? ResultContent,
        int ResultBytes,
        string? ResultSha256);
}
