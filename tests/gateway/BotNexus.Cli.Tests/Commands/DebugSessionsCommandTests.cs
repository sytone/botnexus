using BotNexus.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for the debug sessions CLI subcommands that read sessions.db directly.
/// </summary>
public sealed class DebugSessionsCommandTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DebugSessionsCommand _command;

    public DebugSessionsCommandTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}", "sessions.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _command = new DebugSessionsCommand(() => _dbPath);
        SetupTestDatabase();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_dbPath)!;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private void SetupTestDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
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

            CREATE TABLE IF NOT EXISTS session_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT,
                role TEXT,
                content TEXT,
                timestamp TEXT,
                tool_name TEXT,
                tool_call_id TEXT,
                is_compaction_summary INTEGER NOT NULL DEFAULT 0,
                is_crash_sentinel INTEGER NOT NULL DEFAULT 0,
                is_history INTEGER NOT NULL DEFAULT 0,
                trigger_type TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // Insert test sessions
        InsertSession(connection, "sess-001", "signalr", "user-1", "Active", "conv-a", "2026-06-01T10:00:00Z");
        InsertSession(connection, "sess-002", "telegram", "user-2", "Sealed", "conv-b", "2026-06-02T12:00:00Z");
        InsertSession(connection, "sess-003", "signalr", "user-1", "Active", "conv-a", "2026-06-03T08:00:00Z");

        // Insert history entries
        InsertHistory(connection, "sess-001", "user", "Hello", "2026-06-01T10:01:00Z");
        InsertHistory(connection, "sess-001", "assistant", "Hi there!", "2026-06-01T10:01:05Z");
        InsertHistory(connection, "sess-001", "system", "Compaction summary: user greeted agent.", "2026-06-01T10:02:00Z", isCompaction: true);
        InsertHistory(connection, "sess-002", "user", "Test message", "2026-06-02T12:01:00Z");
    }

    private static void InsertSession(SqliteConnection conn, string id, string channelType, string callerId, string status, string conversationId, string createdAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, channel_type, caller_id, status, conversation_id, created_at, updated_at)
            VALUES ($id, $channelType, $callerId, $status, $conversationId, $createdAt, $createdAt)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$channelType", channelType);
        cmd.Parameters.AddWithValue("$callerId", callerId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$conversationId", conversationId);
        cmd.Parameters.AddWithValue("$createdAt", createdAt);
        cmd.ExecuteNonQuery();
    }

    private static void InsertHistory(SqliteConnection conn, string sessionId, string role, string content, string timestamp, bool isCompaction = false)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_history (session_id, role, content, timestamp, is_compaction_summary)
            VALUES ($sessionId, $role, $content, $timestamp, $isCompaction)
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$timestamp", timestamp);
        cmd.Parameters.AddWithValue("$isCompaction", isCompaction ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    // ────────────────────────────────────────────────────────────────
    //  list
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsAllSessions()
    {
        var result = await _command.ExecuteListAsync(status: null, limit: 100, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, result.Sessions.Count);
    }

    [Fact]
    public async Task List_FilterByStatus_ReturnsMatching()
    {
        var result = await _command.ExecuteListAsync(status: "Active", limit: 100, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Sessions.Count);
        Assert.All(result.Sessions, s => Assert.Equal("Active", s.Status));
    }

    [Fact]
    public async Task List_WithLimit_RespectsLimit()
    {
        var result = await _command.ExecuteListAsync(status: null, limit: 2, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Sessions.Count);
    }

    // ────────────────────────────────────────────────────────────────
    //  get
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ExistingSession_ReturnsDetails()
    {
        var result = await _command.ExecuteGetAsync("sess-001", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Session);
        Assert.Equal("sess-001", result.Session.Id);
        Assert.Equal("signalr", result.Session.ChannelType);
        Assert.Equal("Active", result.Session.Status);
        Assert.Equal(3, result.Session.MessageCount);
    }

    [Fact]
    public async Task Get_NonExistentSession_ReturnsError()
    {
        var result = await _command.ExecuteGetAsync("nonexistent", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.Session);
    }

    // ────────────────────────────────────────────────────────────────
    //  compaction
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Compaction_WithSummary_ReturnsContent()
    {
        var result = await _command.ExecuteCompactionAsync("sess-001", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CompactionStatus.HasContent, result.Status);
        Assert.Contains("user greeted agent", result.Content);
    }

    [Fact]
    public async Task Compaction_NoSummary_ReturnsNone()
    {
        var result = await _command.ExecuteCompactionAsync("sess-002", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CompactionStatus.None, result.Status);
    }

    [Fact]
    public async Task Compaction_NonExistentSession_ReturnsError()
    {
        var result = await _command.ExecuteCompactionAsync("nonexistent", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
    }

    // ────────────────────────────────────────────────────────────────
    //  stats
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_ReturnsAggregates()
    {
        var result = await _command.ExecuteStatsAsync(CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, result.TotalSessions);
        Assert.Equal(2, result.ActiveSessions);
        Assert.Equal(1, result.SealedSessions);
        Assert.True(result.DbSizeBytes > 0);
    }

    // ────────────────────────────────────────────────────────────────
    //  error handling
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_MissingDb_ReturnsError()
    {
        var missingCommand = new DebugSessionsCommand(() => "/nonexistent/path/sessions.db");

        var result = await missingCommand.ExecuteListAsync(status: null, limit: 100, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
    }
}
