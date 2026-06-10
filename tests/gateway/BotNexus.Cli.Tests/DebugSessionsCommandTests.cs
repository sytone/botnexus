using BotNexus.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests;

public sealed class DebugSessionsCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public DebugSessionsCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sessions.db");
        CreateTestDatabase();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void CreateTestDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE sessions (
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
            CREATE TABLE session_history (
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
        InsertSession(connection, "sess-001", "conv-alpha", "signalr", "user1", "standard", "active", @"{""key"":""val""}", "2026-06-10T10:00:00Z", "2026-06-10T10:05:00Z");
        InsertSession(connection, "sess-002", "conv-beta", "signalr", "user2", "standard", "sealed", null, "2026-06-09T08:00:00Z", "2026-06-09T09:30:00Z");
        InsertSession(connection, "sess-003", "conv-alpha", "telegram", "user3", "cron", "active", null, "2026-06-10T11:00:00Z", "2026-06-10T11:01:00Z");

        // Insert messages for sess-001
        InsertHistory(connection, "sess-001", "user", "Hello", "2026-06-10T10:00:00Z", isCompaction: false);
        InsertHistory(connection, "sess-001", "assistant", "Hi there!", "2026-06-10T10:00:01Z", isCompaction: false);
        InsertHistory(connection, "sess-001", "system", "Summary of conversation", "2026-06-10T10:05:00Z", isCompaction: true);

        // Insert messages for sess-002 (no compaction)
        InsertHistory(connection, "sess-002", "user", "Test", "2026-06-09T08:00:00Z", isCompaction: false);

        // Insert empty compaction for sess-003
        InsertHistory(connection, "sess-003", "system", "", "2026-06-10T11:01:00Z", isCompaction: true);
    }

    private static void InsertSession(SqliteConnection conn, string id, string convId, string channel, string caller, string type, string status, string? metadata, string created, string updated)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (id, conversation_id, channel_type, caller_id, session_type, status, metadata, created_at, updated_at) VALUES (@id, @conv, @ch, @caller, @type, @status, @meta, @created, @updated)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@conv", convId);
        cmd.Parameters.AddWithValue("@ch", channel);
        cmd.Parameters.AddWithValue("@caller", caller);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@meta", (object?)metadata ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", created);
        cmd.Parameters.AddWithValue("@updated", updated);
        cmd.ExecuteNonQuery();
    }

    private static void InsertHistory(SqliteConnection conn, string sessionId, string role, string content, string timestamp, bool isCompaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO session_history (session_id, role, content, timestamp, is_compaction_summary) VALUES (@sid, @role, @content, @ts, @comp)";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@ts", timestamp);
        cmd.Parameters.AddWithValue("@comp", isCompaction ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ResolveSessionsDb_DefaultTarget_ReturnsExpectedPath()
    {
        var path = DebugSessionsCommand.ResolveSessionsDb(null);
        path.ShouldEndWith("sessions.db");
        // Should resolve to either ~/.botnexus/sessions.db or BOTNEXUS_HOME/sessions.db
        Path.GetFileName(path).ShouldBe("sessions.db");
    }

    [Fact]
    public void ResolveSessionsDb_WithTarget_ReturnsTargetPath()
    {
        var path = DebugSessionsCommand.ResolveSessionsDb(_tempDir);
        path.ShouldBe(Path.Combine(_tempDir, "sessions.db"));
    }

    [Fact]
    public void ExecuteList_ReturnsAllSessions()
    {
        var result = DebugSessionsCommand.ExecuteList(_dbPath, null, null, 20, "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteList_FilterByStatus_ReturnsFilteredSessions()
    {
        var result = DebugSessionsCommand.ExecuteList(_dbPath, null, "active", 20, "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteList_MissingDb_ReturnsError()
    {
        var result = DebugSessionsCommand.ExecuteList("/nonexistent/path/sessions.db", null, null, 20, "table");
        result.ShouldBe(1);
    }

    [Fact]
    public void ExecuteGet_ExistingSession_ReturnsSuccess()
    {
        var result = DebugSessionsCommand.ExecuteGet(_dbPath, "sess-001", "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteGet_NonexistentSession_ReturnsError()
    {
        var result = DebugSessionsCommand.ExecuteGet(_dbPath, "sess-999", "table");
        result.ShouldBe(1);
    }

    [Fact]
    public void ExecuteGet_MissingDb_ReturnsError()
    {
        var result = DebugSessionsCommand.ExecuteGet("/nonexistent/path/sessions.db", "sess-001", "table");
        result.ShouldBe(1);
    }

    [Fact]
    public void ExecuteCompaction_SessionWithCompaction_ReturnsSuccess()
    {
        var result = DebugSessionsCommand.ExecuteCompaction(_dbPath, "sess-001", "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteCompaction_SessionWithEmptyCompaction_ReturnsSuccess()
    {
        var result = DebugSessionsCommand.ExecuteCompaction(_dbPath, "sess-003", "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteCompaction_SessionWithNoCompaction_ReturnsSuccess()
    {
        var result = DebugSessionsCommand.ExecuteCompaction(_dbPath, "sess-002", "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteCompaction_MissingDb_ReturnsError()
    {
        var result = DebugSessionsCommand.ExecuteCompaction("/nonexistent/path/sessions.db", "sess-001", "table");
        result.ShouldBe(1);
    }

    [Fact]
    public void ExecuteStats_ReturnsSuccess()
    {
        var result = DebugSessionsCommand.ExecuteStats(_dbPath, "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void ExecuteStats_MissingDb_ReturnsError()
    {
        var result = DebugSessionsCommand.ExecuteStats("/nonexistent/path/sessions.db", "table");
        result.ShouldBe(1);
    }

    [Fact]
    public void BuildListQuery_NoFilters_ReturnsBasicQuery()
    {
        var sql = DebugSessionsCommand.BuildListQuery(null, null, 20);
        sql.ShouldContain("FROM sessions");
        sql.ShouldContain("LIMIT @limit");
        // No filter WHERE conditions present
        sql.ShouldNotContain("s.status = @status");
        sql.ShouldNotContain("s.conversation_id LIKE");
    }

    [Fact]
    public void BuildListQuery_WithStatusFilter_IncludesWhereClause()
    {
        var sql = DebugSessionsCommand.BuildListQuery(null, "active", 10);
        sql.ShouldContain("s.status = @status");
    }

    [Fact]
    public void BuildListQuery_StatusAll_NoTopLevelWhereClause()
    {
        var sql = DebugSessionsCommand.BuildListQuery(null, "all", 50);
        // "all" should not add a status filter
        sql.ShouldNotContain("s.status = @status");
    }
}
