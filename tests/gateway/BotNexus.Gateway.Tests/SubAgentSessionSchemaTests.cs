using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using BotNexus.Gateway.Abstractions.Conversations;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Verifies that sub_agent_sessions table and its indexes are created as part of the
/// sessions.db schema initialisation (Issue #807, Part of #785).
/// </summary>
public sealed class SubAgentSessionSchemaTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly string _sessionDbPath;
    private readonly string _conversationDbPath;

    public SubAgentSessionSchemaTests()
    {
        _directoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "SubAgentSessionSchemaTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        _sessionDbPath = Path.Combine(_directoryPath, "sessions.db");
        _conversationDbPath = Path.Combine(_directoryPath, "conversations.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
            Directory.Delete(_directoryPath, recursive: true);
    }

    [Fact]
    public async Task EnsureCreated_CreatesFreshDb_SubAgentSessionsTableExists()
    {
        var store = CreateSessionStore();
        // Trigger lazy initialization via a public method call
        await store.GetAsync(BotNexus.Domain.Primitives.SessionId.From("nonexistent"));

        await using var conn = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False");
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sub_agent_sessions'";
        var result = await cmd.ExecuteScalarAsync();

        result.ShouldNotBeNull("sub_agent_sessions table should be created by EnsureCreatedAsync");
        result!.ToString().ShouldBe("sub_agent_sessions");
    }

    [Fact]
    public async Task EnsureCreated_SubAgentSessionsTable_HasRequiredColumns()
    {
        var store = CreateSessionStore();
        await store.GetAsync(BotNexus.Domain.Primitives.SessionId.From("nonexistent"));

        await using var conn = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False");
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(sub_agent_sessions)";

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1)); // column index 1 = name

        columns.ShouldContain("id");
        columns.ShouldContain("parent_session_id");
        columns.ShouldContain("parent_agent_id");
        columns.ShouldContain("child_agent_id");
        columns.ShouldContain("archetype");
        columns.ShouldContain("started_at");
        columns.ShouldContain("ended_at");
        columns.ShouldContain("status");
    }

    [Fact]
    public async Task EnsureCreated_SubAgentSessionsTable_HasParentSessionIdIndex()
    {
        var store = CreateSessionStore();
        await store.GetAsync(BotNexus.Domain.Primitives.SessionId.From("nonexistent"));

        await using var conn = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False");
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='sub_agent_sessions' AND name='idx_sub_agent_sessions_parent'";
        var result = await cmd.ExecuteScalarAsync();

        result.ShouldNotBeNull("idx_sub_agent_sessions_parent index should be created");
        result!.ToString().ShouldBe("idx_sub_agent_sessions_parent");
    }

    [Fact]
    public async Task EnsureCreated_ExistingDb_SubAgentSessionsTableAddedOnUpgrade()
    {
        // Simulate upgrading an existing DB that does NOT yet have sub_agent_sessions.
        // Create the old schema manually (sessions + session_history only), then
        // open via SqliteSessionStore — EnsureCreatedAsync should add the new table.
        await using (var conn = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
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
            await cmd.ExecuteNonQueryAsync();
        }

        // Now open via the store — should add sub_agent_sessions without error.
        var store = CreateSessionStore();
        await store.GetAsync(BotNexus.Domain.Primitives.SessionId.From("nonexistent"));

        await using var checkConn = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False");
        await checkConn.OpenAsync();

        await using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sub_agent_sessions'";
        var result = await checkCmd.ExecuteScalarAsync();

        result.ShouldNotBeNull("sub_agent_sessions table should be created when upgrading an existing DB");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private SqliteSessionStore CreateSessionStore()
    {
        var convStore = new SqliteConversationStore(
            $"Data Source={_conversationDbPath};Pooling=False",
            NullLogger<SqliteConversationStore>.Instance);
        return new SqliteSessionStore(
            $"Data Source={_sessionDbPath};Pooling=False",
            NullLogger<SqliteSessionStore>.Instance,
            convStore);
    }
}
