using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests;

public sealed class DebugLogsCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logsDir;

    public DebugLogsCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-logtest-{Guid.NewGuid():N}");
        _logsDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(_logsDir);
        CreateTestLogFiles();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void CreateTestLogFiles()
    {
        // Create a log file for 2026-06-10 hour 14
        var file1 = Path.Combine(_logsDir, "botnexus-2026061014.log");
        File.WriteAllLines(file1, [
            "2026-06-10 14:00:01.123 +00:00 [INF] Gateway started on port 5005",
            "2026-06-10 14:00:02.456 +00:00 [DBG] Loading agent configuration for farnsworth",
            "2026-06-10 14:01:00.789 +00:00 [INF] Session sess-abc-123 created for agent nova",
            "2026-06-10 14:02:30.100 +00:00 [WRN] Compaction for sess-abc-123 took 45s (threshold: 30s)",
            "2026-06-10 14:05:00.000 +00:00 [ERR] Provider timeout: Copilot returned 503"
        ]);

        // Create a log file for 2026-06-10 hour 15
        var file2 = Path.Combine(_logsDir, "botnexus-2026061015.log");
        File.WriteAllLines(file2, [
            "2026-06-10 15:00:00.000 +00:00 [INF] Cron job heartbeat:farnsworth fired",
            "2026-06-10 15:00:01.000 +00:00 [INF] Session sess-def-456 created for agent farnsworth",
            "2026-06-10 15:01:00.000 +00:00 [ERR] Tool execution failed: read returned empty",
            "2026-06-10 15:01:00.001 +00:00 [ERR]   at BotNexus.Gateway.Tools.ReadTool.Execute()",
            "2026-06-10 15:02:00.000 +00:00 [INF] Session sess-abc-123 sealed (compaction complete)"
        ]);
    }

    [Fact]
    public void ResolveLogsDir_WithTarget_ReturnsLogsSubdir()
    {
        var dir = DebugLogsCommand.ResolveLogsDir(_tempDir);
        dir.ShouldBe(_logsDir);
    }

    [Fact]
    public void ResolveLogsDir_DefaultTarget_EndsWithLogs()
    {
        var dir = DebugLogsCommand.ResolveLogsDir(null);
        dir.ShouldEndWith("logs");
    }

    [Fact]
    public void GetLogFilesSorted_ReturnsFilesInOrder()
    {
        var files = DebugLogsCommand.GetLogFilesSorted(_logsDir);
        files.Count.ShouldBe(2);
        Path.GetFileName(files[0]).ShouldBe("botnexus-2026061014.log");
        Path.GetFileName(files[1]).ShouldBe("botnexus-2026061015.log");
    }

    [Fact]
    public void GetLogFilesSorted_NonexistentDir_ReturnsEmpty()
    {
        var files = DebugLogsCommand.GetLogFilesSorted("/nonexistent/path");
        files.ShouldBeEmpty();
    }

    [Fact]
    public void TailLines_ReturnsLastNLines()
    {
        var lines = DebugLogsCommand.TailLines(_logsDir, 3, null);
        lines.Count.ShouldBe(3);
        // Last 3 lines from file2 (most recent)
        lines[2].Message!.ShouldContain("sess-abc-123 sealed");
    }

    [Fact]
    public void TailLines_WithLevelFilter_ReturnsOnlyMatchingLevel()
    {
        var lines = DebugLogsCommand.TailLines(_logsDir, 50, "error");
        lines.Count.ShouldBe(3); // 1 ERR from file1, 2 ERR from file2
        foreach (var line in lines)
        {
            line.Level.ShouldBe("ERR");
        }
    }

    [Fact]
    public void TailLines_EmptyDir_ReturnsEmpty()
    {
        var emptyDir = Path.Combine(_tempDir, "empty-logs");
        Directory.CreateDirectory(emptyDir);
        var lines = DebugLogsCommand.TailLines(emptyDir, 10, null);
        lines.ShouldBeEmpty();
    }

    [Fact]
    public void SearchLines_FindsTerm()
    {
        var lines = DebugLogsCommand.SearchLines(_logsDir, "sess-abc-123", null, 50);
        lines.Count.ShouldBe(3); // appears in file1 (2 lines) and file2 (1 line)
    }

    [Fact]
    public void SearchLines_CaseInsensitive()
    {
        var lines = DebugLogsCommand.SearchLines(_logsDir, "GATEWAY STARTED", null, 50);
        lines.Count.ShouldBe(1);
    }

    [Fact]
    public void SearchLines_WithSince_SkipsOlderFiles()
    {
        // Since 2026-06-10 16:00 should skip both files (file2 covers hour 15)
        var since = new DateTime(2026, 6, 10, 16, 0, 0);
        var lines = DebugLogsCommand.SearchLines(_logsDir, "sess-abc-123", since, 50);
        lines.Count.ShouldBe(0);
    }

    [Fact]
    public void SearchLines_RespectsLimit()
    {
        var lines = DebugLogsCommand.SearchLines(_logsDir, "2026", null, 2);
        lines.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseLogLine_ValidLine_ParsesCorrectly()
    {
        var entry = DebugLogsCommand.ParseLogLine("2026-06-10 14:00:01.123 +00:00 [INF] Gateway started");
        entry.ShouldNotBeNull();
        entry.Timestamp.ShouldBe("2026-06-10 14:00:01.123");
        entry.Offset.ShouldBe("+00:00");
        entry.Level.ShouldBe("INF");
        entry.Message.ShouldBe("Gateway started");
    }

    [Fact]
    public void ParseLogLine_EmptyLine_ReturnsNull()
    {
        var entry = DebugLogsCommand.ParseLogLine("");
        entry.ShouldBeNull();
    }

    [Fact]
    public void ParseLogLine_NonStructuredLine_ReturnsRawMessage()
    {
        var entry = DebugLogsCommand.ParseLogLine("  at System.Runtime.Something.Method()");
        entry.ShouldNotBeNull();
        entry.Timestamp.ShouldBeNull();
        entry.Level.ShouldBeNull();
        entry.Message!.ShouldContain("System.Runtime");
    }

    [Fact]
    public void ParseFileTimestamp_ValidTenDigit_ParsesCorrectly()
    {
        var dt = DebugLogsCommand.ParseFileTimestamp("/logs/botnexus-2026061014.log");
        dt.ShouldNotBeNull();
        dt.Value.ShouldBe(new DateTime(2026, 6, 10, 14, 0, 0));
    }

    [Fact]
    public void ParseFileTimestamp_ValidNineDigit_ParsesCorrectly()
    {
        var dt = DebugLogsCommand.ParseFileTimestamp("/logs/botnexus-202606109.log");
        dt.ShouldNotBeNull();
        dt.Value.ShouldBe(new DateTime(2026, 6, 10, 9, 0, 0));
    }

    [Fact]
    public void ParseFileTimestamp_InvalidFile_ReturnsNull()
    {
        var dt = DebugLogsCommand.ParseFileTimestamp("/logs/other-file.log");
        dt.ShouldBeNull();
    }

    [Fact]
    public void ExecuteTail_MissingDir_ReturnsError()
    {
        var result = DebugLogsCommand.ExecuteTail("/nonexistent/logs", 10, null, "table");
        result.ShouldBe(1);
    }

    [Fact]
    public void ExecuteTail_ValidDir_ReturnsSuccess()
    {
        var result = DebugLogsCommand.ExecuteTail(_logsDir, 5, null, "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteSearch_MissingDir_ReturnsError()
    {
        var result = DebugLogsCommand.ExecuteSearch("/nonexistent/logs", "test", null, 10, "table");
        result.ShouldBe(1);
    }

    [Fact]
    public void ExecuteSearch_ValidDir_ReturnsSuccess()
    {
        var result = DebugLogsCommand.ExecuteSearch(_logsDir, "Gateway", null, 10, "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteSearch_InvalidSince_ReturnsError()
    {
        var result = DebugLogsCommand.ExecuteSearch(_logsDir, "test", "not-a-date", 10, "table");
        result.ShouldBe(1);
    }
}
