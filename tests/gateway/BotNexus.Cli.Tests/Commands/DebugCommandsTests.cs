using System.Text.Json;
using BotNexus.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for DebugCommands -- offline SQLite reads and log file inspection.
/// These tests create in-memory or temp SQLite databases; no gateway required.
/// </summary>
public sealed class DebugCommandsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _logsDir;

    public DebugCommandsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sessions.db");
        _logsDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(_logsDir);
        CreateSeedDatabase(_dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── sessions list ─────────────────────────────────────────────────────

    [Fact]
    public async Task SessionsList_WithNoFilters_ReturnsAllSessions()
    {
        var rows = await CaptureSessionsListAsync(agentFilter: null, statusFilter: null, limit: 100);
        rows.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task SessionsList_WithAgentFilter_ReturnsOnlyMatchingAgent()
    {
        var rows = await CaptureSessionsListAsync(agentFilter: "nova", statusFilter: null, limit: 100);
        rows.ShouldAllBe(r => r["agent_id"] == "nova");
    }

    [Fact]
    public async Task SessionsList_WithStatusFilter_ReturnsOnlyMatchingStatus()
    {
        var rows = await CaptureSessionsListAsync(agentFilter: null, statusFilter: "active", limit: 100);
        rows.ShouldAllBe(r => r["status"] == "active");
    }

    [Fact]
    public async Task SessionsList_WithLimit_RespectsLimit()
    {
        var rows = await CaptureSessionsListAsync(agentFilter: null, statusFilter: null, limit: 1);
        rows.Count.ShouldBe(1);
    }

    // ── sessions get ──────────────────────────────────────────────────────

    [Fact]
    public async Task SessionsGet_ExistingSession_Returns0()
    {
        var result = await DebugCommands.ExecuteSessionsGetAsync(_tempDir, "session-1", "json", default);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task SessionsGet_UnknownSession_Returns1()
    {
        var result = await DebugCommands.ExecuteSessionsGetAsync(_tempDir, "no-such-session", "json", default);
        result.ShouldBe(1);
    }

    // ── sessions compaction ───────────────────────────────────────────────

    [Fact]
    public async Task SessionsCompaction_SessionWithSummary_Returns0()
    {
        // Seed a compaction entry
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_history (session_id, role, content, is_compaction_summary)
            VALUES ('session-1', 'system', 'Compaction text here.', 1)
            """;
        await cmd.ExecuteNonQueryAsync();

        var result = await DebugCommands.ExecuteSessionsCompactionAsync(_tempDir, "session-1", default);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task SessionsCompaction_SessionWithNoCompaction_Returns0WithNoneMessage()
    {
        var result = await DebugCommands.ExecuteSessionsCompactionAsync(_tempDir, "session-2", default);
        result.ShouldBe(0); // [NONE] message but exit 0
    }

    [Fact]
    public async Task SessionsCompaction_UnknownSession_Returns1()
    {
        var result = await DebugCommands.ExecuteSessionsCompactionAsync(_tempDir, "no-such-session", default);
        result.ShouldBe(1);
    }

    // ── sessions stats ────────────────────────────────────────────────────

    [Fact]
    public async Task SessionsStats_ReturnsZeroExitCode()
    {
        var result = await DebugCommands.ExecuteSessionsStatsAsync(_tempDir, "json", default);
        result.ShouldBe(0);
    }

    // ── db tables ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DbTables_ReturnsSessions_AndHistory()
    {
        var result = await DebugCommands.ExecuteDbTablesAsync(_tempDir, "sessions", "json", default);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task DbTables_MissingDb_Returns1()
    {
        var result = await DebugCommands.ExecuteDbTablesAsync(_tempDir, "memory", "json", default);
        result.ShouldBe(1);
    }

    // ── db schema ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DbSchema_ReturnsZeroExitCode()
    {
        var result = await DebugCommands.ExecuteDbSchemaAsync(_tempDir, "sessions", default);
        result.ShouldBe(0);
    }

    // ── db size ───────────────────────────────────────────────────────────

    [Fact]
    public void DbSize_ReturnsZeroExitCode()
    {
        var result = DebugCommands.ExecuteDbSizeAsync(_tempDir);
        result.ShouldBe(0);
    }

    // ── logs tail ─────────────────────────────────────────────────────────

    [Fact]
    public void LogsTail_WithNoLogs_ReturnsZero()
    {
        // logs dir exists but empty
        var result = DebugCommands.ExecuteLogsTailAsync(_tempDir, null, 10);
        result.ShouldBe(0);
    }

    [Fact]
    public void LogsTail_WithLogFile_ReturnsZero()
    {
        var logFile = Path.Combine(_logsDir, $"botnexus-{DateTime.Now:yyyyMMddHH}.log");
        File.WriteAllLines(logFile, ["2026-05-21 12:00:00 [INF] Hello world", "2026-05-21 12:01:00 [ERR] Something failed"]);

        var result = DebugCommands.ExecuteLogsTailAsync(_tempDir, null, 10);
        result.ShouldBe(0);
    }

    [Fact]
    public void LogsSearch_WithMatchingTerm_ReturnsZero()
    {
        var logFile = Path.Combine(_logsDir, $"botnexus-{DateTime.Now:yyyyMMddHH}.log");
        File.WriteAllLines(logFile, ["2026-05-21 12:00:00 [INF] Session abc123 started"]);

        var result = DebugCommands.ExecuteLogsSearchAsync(_tempDir, "abc123", 10);
        result.ShouldBe(0);
    }

    [Fact]
    public void LogsSearch_WithNoTerm_Returns1()
    {
        var result = DebugCommands.ExecuteLogsSearchAsync(_tempDir, null, 10);
        result.ShouldBe(1);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    [Fact]
    public void ResolveDbPath_ReturnsCorrectPath()
    {
        var path = DebugCommands.ResolveDbPath(_tempDir, "sessions");
        path.ShouldBe(Path.Combine(_tempDir, "sessions.db"));
    }

    // ── private helpers ───────────────────────────────────────────────────

    private async Task<List<Dictionary<string, string>>> CaptureSessionsListAsync(
        string? agentFilter, string? statusFilter, int limit)
    {
        // Redirect console to capture JSON output
        var oldOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            await DebugCommands.ExecuteSessionsListAsync(_tempDir, agentFilter, statusFilter, limit, "json", default);
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        var json = sw.ToString().Trim();
        return JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? [];
    }

    private static void CreateSeedDatabase(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var create = conn.CreateCommand();
        create.CommandText = """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                agent_id TEXT,
                channel_type TEXT,
                caller_id TEXT,
                session_type TEXT,
                participants_json TEXT,
                status TEXT,
                metadata TEXT,
                created_at TEXT,
                updated_at TEXT,
                conversation_id TEXT
            );
            CREATE TABLE session_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT,
                role TEXT,
                content TEXT,
                timestamp TEXT,
                tool_name TEXT,
                tool_call_id TEXT,
                is_compaction_summary INTEGER DEFAULT 0,
                tool_args TEXT,
                tool_is_error INTEGER DEFAULT 0,
                is_crash_sentinel INTEGER DEFAULT 0
            );
            INSERT INTO sessions (id, agent_id, session_type, status, created_at, updated_at)
                VALUES ('session-1', 'nova', 'user-agent', 'active', '2026-05-21T00:00:00Z', '2026-05-21T01:00:00Z');
            INSERT INTO sessions (id, agent_id, session_type, status, created_at, updated_at)
                VALUES ('session-2', 'farnsworth', 'cron', 'sealed', '2026-05-21T00:00:00Z', '2026-05-21T02:00:00Z');
            INSERT INTO session_history (session_id, role, content)
                VALUES ('session-1', 'user', 'Hello');
            """;
        create.ExecuteNonQuery();
    }
}
