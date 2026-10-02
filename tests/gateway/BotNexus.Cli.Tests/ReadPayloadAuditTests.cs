using BotNexus.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests;

public sealed class ReadPayloadAuditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"botnexus-read-audit-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public ReadPayloadAuditTests()
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
    public void CreateReport_CountsOnlyRepeatedFullBodiesForTheSameSlice()
    {
        var report = ReadPayloadAudit.CreateReport(
            _databasePath,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        report.SuccessfulReadResults.ShouldBe(9);
        report.RepeatedFullPayloads.ShouldBe(1);
        report.RepeatedFullPayloadBytes.ShouldBe(10);
        report.UnchangedMarkers.ShouldBe(1);
        report.ChangedSameSliceResults.ShouldBe(4);
        report.DifferentSliceResults.ShouldBe(1);
        report.FirstSliceResults.ShouldBe(3);
        report.RepeatedPayloadsPerThousand.ShouldBe(111.11m);
        report.MaximumRepeatedPayloadsPerThousand.ShouldBe(5m);
        report.MeetsThreshold.ShouldBeFalse();
    }

    [Fact]
    public void CreateReport_ExcludesErrorsAndRowsOutsideTheHalfOpenWindow()
    {
        var report = ReadPayloadAudit.CreateReport(
            _databasePath,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        report.SuccessfulReadResults.ShouldBe(9);
        report.ExcludedErrorResults.ShouldBe(1);
        report.WindowStart.ShouldBe(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        report.WindowEnd.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void CreateReport_IsReadOnly()
    {
        var before = File.ReadAllBytes(_databasePath);

        _ = ReadPayloadAudit.CreateReport(
            _databasePath,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        File.ReadAllBytes(_databasePath).ShouldBe(before);
    }

    [Fact]
    public void CreateReport_RejectsAnEmptyOrReversedWindow()
    {
        var boundary = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Should.Throw<ArgumentOutOfRangeException>(() =>
            ReadPayloadAudit.CreateReport(_databasePath, boundary, boundary));
    }

    private void CreateCorpus()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
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
                message_kind TEXT
            );

            INSERT INTO session_history
                (session_id, role, content, timestamp, tool_name, tool_call_id, tool_args, tool_is_error, message_kind)
            VALUES
                ('s1', 'tool', 'alpha-body', '2026-08-01T00:00:00Z', 'read', 'c1', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'alpha-body', '2026-08-01T00:01:00Z', 'read', 'c2', '{"limit":20,"path":"a.txt","offset":1}', 0, 'tool-result'),
                ('s1', 'tool', '[Unchanged since your earlier read of ''a.txt'' (lines 1-20, 10 chars) in this session.]', '2026-08-01T00:02:00Z', 'read', 'c3', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'beta-body', '2026-08-01T00:03:00Z', 'read', 'c4', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'alpha-body', '2026-08-01T00:04:00Z', 'read', 'c5', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'alpha-body\n…[truncated 50 bytes]', '2026-08-01T00:04:10Z', 'read', 'c5a', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'alpha-body', '2026-08-01T00:04:20Z', 'read', 'c5b', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'other-slice', '2026-08-01T00:05:00Z', 'read', 'c6', '{"path":"a.txt","offset":21,"limit":20}', 0, 'tool-result'),
                ('s2', 'tool', 'alpha-body', '2026-08-01T00:06:00Z', 'read', 'c7', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'failure', '2026-08-01T00:07:00Z', 'read', 'c8', '{"path":"a.txt","offset":1,"limit":20}', 1, 'tool-result'),
                ('s1', 'tool', 'alpha-body', '2026-07-31T23:59:59Z', 'read', 'c9', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'alpha-body', '2026-09-01T00:00:00Z', 'read', 'c10', '{"path":"a.txt","offset":1,"limit":20}', 0, 'tool-result'),
                ('s1', 'tool', 'alpha-body', '2026-08-01T00:08:00Z', 'write', 'c11', '{"path":"a.txt"}', 0, 'tool-result'),
                ('s1', 'assistant', 'alpha-body', '2026-08-01T00:09:00Z', 'read', 'c12', '{"path":"a.txt"}', 0, 'tool-result'),
                ('s1', 'tool', 'alpha-body', '2026-08-01T00:10:00Z', 'read', 'c13', '{"path":"a.txt"}', 0, 'tool-start');
            """;
        command.ExecuteNonQuery();
    }
}
