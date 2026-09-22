using BotNexus.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests;

public sealed class ToolRetentionPreviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"botnexus-retention-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public ToolRetentionPreviewTests()
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
    public void CreateReport_ClassifiesEligibleAndProtectedRowsWithoutMutatingTheStore()
    {
        var before = File.ReadAllBytes(_databasePath);

        var report = ToolRetentionPreview.CreateReport(
            _databasePath,
            olderThanDays: 30,
            now: new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));

        report.CandidateInvocations.ShouldBe(1);
        report.CandidateRows.ShouldBe(2);
        report.CandidateContentBytes.ShouldBe(12);
        report.CandidateArgumentBytes.ShouldBe(12);
        report.DuplicatedCandidateArgumentBytes.ShouldBe(24);
        report.EstimatedReclaimableBytes.ShouldBe(24);
        report.ProtectedRows["active-session"].ShouldBe(2);
        report.ProtectedRows["recent"].ShouldBe(2);
        report.ProtectedRows["error"].ShouldBe(2);
        report.ProtectedRows["incomplete"].ShouldBe(1);
        report.ProtectedRows["mutation"].ShouldBe(2);
        report.BySessionState["sealed"].CandidateInvocations.ShouldBe(1);
        report.ByToolClass["read"].CandidateInvocations.ShouldBe(1);
        report.ByOutcome["success"].CandidateInvocations.ShouldBe(1);
        File.ReadAllBytes(_databasePath).ShouldBe(before);
    }

    private void CreateCorpus()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sessions (id TEXT PRIMARY KEY, status TEXT);
            CREATE TABLE session_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT,
                role TEXT,
                content TEXT,
                timestamp TEXT,
                tool_name TEXT,
                tool_call_id TEXT,
                tool_args TEXT,
                tool_is_error INTEGER NOT NULL DEFAULT 0,
                is_history INTEGER NOT NULL DEFAULT 0,
                message_kind TEXT
            );
            INSERT INTO sessions VALUES ('eligible', 'Sealed'), ('active', 'Active'), ('recent', 'Expired'), ('failed', 'Sealed'), ('incomplete', 'Sealed'), ('mutation', 'Sealed');

            INSERT INTO session_history (session_id, role, content, timestamp, tool_name, tool_call_id, tool_args, message_kind) VALUES
              ('eligible', 'assistant', 'start!', '2026-07-01T00:00:00Z', 'read', 'c1', '{"path":"a"}', 'tool-start'),
              ('eligible', 'tool', 'result', '2026-07-01T00:00:01Z', 'read', 'c1', '{"path":"a"}', 'tool-result'),
              ('active', 'assistant', 'start', '2026-07-01T00:00:00Z', 'read', 'c2', '{}', 'tool-start'),
              ('active', 'tool', 'result', '2026-07-01T00:00:01Z', 'read', 'c2', '{}', 'tool-result'),
              ('recent', 'assistant', 'start', '2026-09-10T00:00:00Z', 'read', 'c3', '{}', 'tool-start'),
              ('recent', 'tool', 'result', '2026-09-10T00:00:01Z', 'read', 'c3', '{}', 'tool-result'),
              ('failed', 'assistant', 'start', '2026-07-01T00:00:00Z', 'read', 'c4', '{}', 'tool-start'),
              ('failed', 'tool', 'failure', '2026-07-01T00:00:01Z', 'read', 'c4', '{}', 'tool-result'),
              ('incomplete', 'assistant', 'start', '2026-07-01T00:00:00Z', 'read', 'c5', '{}', 'tool-start'),
              ('mutation', 'assistant', 'start', '2026-07-01T00:00:00Z', 'github_issue_create', 'c6', '{}', 'tool-start'),
              ('mutation', 'tool', 'created', '2026-07-01T00:00:01Z', 'github_issue_create', 'c6', '{}', 'tool-result');
            UPDATE session_history SET tool_is_error = 1 WHERE session_id = 'failed' AND message_kind = 'tool-result';
            """;
        command.ExecuteNonQuery();
    }
}
