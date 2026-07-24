using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.DebugTool;
using Microsoft.Data.Sqlite;

namespace BotNexus.Extensions.DebugTool.Tests;

public sealed class DebugToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _agentId = "test-agent";

    public DebugToolTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"debug-tool-test-{Guid.NewGuid():N}.sqlite");
        InitializeTestDatabase();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private void InitializeTestDatabase()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE conversations (
                id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                status TEXT DEFAULT 'active',
                kind TEXT DEFAULT 'chat',
                title TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                status TEXT DEFAULT 'Active',
                channel_type TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (conversation_id) REFERENCES conversations(id)
            );

            CREATE TABLE session_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                sequence_id INTEGER NOT NULL,
                role TEXT NOT NULL,
                content TEXT,
                timestamp TEXT NOT NULL,
                FOREIGN KEY (session_id) REFERENCES sessions(id)
            );

            CREATE TABLE sub_agent_sessions (
                id TEXT PRIMARY KEY,
                parent_session_id TEXT NOT NULL,
                sub_agent_id TEXT NOT NULL,
                objective TEXT,
                status TEXT DEFAULT 'Running',
                started_at TEXT NOT NULL,
                ended_at TEXT,
                FOREIGN KEY (parent_session_id) REFERENCES sessions(id)
            );

            -- Test data: conversations
            INSERT INTO conversations (id, agent_id, status, kind, title, created_at, updated_at)
            VALUES ('conv-1', 'test-agent', 'active', 'chat', 'Test Conversation 1', '2024-01-01T00:00:00Z', '2024-01-01T01:00:00Z');
            INSERT INTO conversations (id, agent_id, status, kind, title, created_at, updated_at)
            VALUES ('conv-2', 'test-agent', 'archived', 'task', 'Test Conversation 2', '2024-01-02T00:00:00Z', '2024-01-02T01:00:00Z');
            INSERT INTO conversations (id, agent_id, status, kind, title, created_at, updated_at)
            VALUES ('conv-3', 'other-agent', 'active', 'chat', 'Other Agent Conv', '2024-01-03T00:00:00Z', '2024-01-03T01:00:00Z');

            -- Test data: sessions
            INSERT INTO sessions (id, conversation_id, status, channel_type, created_at, updated_at)
            VALUES ('sess-1', 'conv-1', 'Active', 'signalr', '2024-01-01T00:00:00Z', '2024-01-01T01:00:00Z');
            INSERT INTO sessions (id, conversation_id, status, channel_type, created_at, updated_at)
            VALUES ('sess-2', 'conv-1', 'Sealed', 'signalr', '2024-01-01T02:00:00Z', '2024-01-01T03:00:00Z');
            INSERT INTO sessions (id, conversation_id, status, channel_type, created_at, updated_at)
            VALUES ('sess-3', 'conv-2', 'Active', 'telegram', '2024-01-02T00:00:00Z', '2024-01-02T01:00:00Z');
            INSERT INTO sessions (id, conversation_id, status, channel_type, created_at, updated_at)
            VALUES ('sess-4', 'conv-3', 'Active', 'signalr', '2024-01-03T00:00:00Z', '2024-01-03T01:00:00Z');

            -- Test data: session_history
            INSERT INTO session_history (session_id, sequence_id, role, content, timestamp)
            VALUES ('sess-1', 1, 'user', 'Hello', '2024-01-01T00:00:01Z');
            INSERT INTO session_history (session_id, sequence_id, role, content, timestamp)
            VALUES ('sess-1', 2, 'assistant', 'Hi there!', '2024-01-01T00:00:02Z');
            INSERT INTO session_history (session_id, sequence_id, role, content, timestamp)
            VALUES ('sess-1', 3, 'user', 'How are you?', '2024-01-01T00:00:03Z');
            INSERT INTO session_history (session_id, sequence_id, role, content, timestamp)
            VALUES ('sess-1', 4, 'assistant', 'I am doing well!', '2024-01-01T00:00:04Z');

            -- Test data: sub_agent_sessions
            INSERT INTO sub_agent_sessions (id, parent_session_id, sub_agent_id, objective, status, started_at, ended_at)
            VALUES ('sub-1', 'sess-1', 'coder-agent', 'Write code', 'Completed', '2024-01-01T00:01:00Z', '2024-01-01T00:02:00Z');
            INSERT INTO sub_agent_sessions (id, parent_session_id, sub_agent_id, objective, status, started_at)
            VALUES ('sub-2', 'sess-1', 'researcher', 'Research topic', 'Running', '2024-01-01T00:03:00Z');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts a single <c>session_history</c> row into the test database. Used by the secret
    /// redaction tests to seed content that contains a secret-shaped token.
    /// </summary>
    private void InsertHistoryRow(string sessionId, int sequenceId, string role, string content)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO session_history (session_id, sequence_id, role, content, timestamp) VALUES (@s, @q, @r, @c, @t)";
        cmd.Parameters.AddWithValue("@s", sessionId);
        cmd.Parameters.AddWithValue("@q", sequenceId);
        cmd.Parameters.AddWithValue("@r", role);
        cmd.Parameters.AddWithValue("@c", content);
        cmd.Parameters.AddWithValue("@t", "2024-01-01T00:05:00Z");
        cmd.ExecuteNonQuery();
    }

    private DebugTool CreateTool(
        DebugToolConfig? config = null,
        IRuntimeStateProvider? runtimeProvider = null,
        BotNexus.Gateway.Abstractions.Security.ISecretRedactor? secretRedactor = null)
    {
        return new DebugTool(_dbPath, _agentId, config ?? new DebugToolConfig(), runtimeProvider, secretRedactor);
    }

    private static IReadOnlyDictionary<string, object?> Args(string action, params (string key, string value)[] extras)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["action"] = action };
        foreach (var (k, v) in extras) d[k] = v;
        return d;
    }

    private static IReadOnlyDictionary<string, object?> ArgsWithInt(string action, params (string key, object value)[] extras)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["action"] = action };
        foreach (var (k, v) in extras) d[k] = v;
        return d;
    }

    private static string TextOf(AgentToolResult r) => r.Content[0].Value;
    private static bool IsError(AgentToolResult r) => r.Content[0].Value.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);

    // ══════════════════════════════════════════════════════════════════════════
    // ValidActions
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ValidActions_ContainsAll8Actions()
    {
        var expected = new[]
        {
            "query_sessions", "query_conversations", "query_history",
            "query_sub_agents", "session_info", "conversation_info",
            "runtime_status", "raw_sql"
        };
        foreach (var a in expected)
            Assert.Contains(a, DebugTool.ValidActions);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Unknown action
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("update")]
    [InlineData("")]
    [InlineData("drop_table")]
    public async Task ExecuteAsync_UnknownAction_ReturnsError(string? action)
    {
        var tool = CreateTool();
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["action"] = action };
        var result = await tool.ExecuteAsync("tc1", args);
        Assert.True(IsError(result));
        Assert.Contains("Unknown action", TextOf(result));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // query_sessions
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task QuerySessions_ReturnsOwnAgentSessions()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_sessions"));
        Assert.False(IsError(result));
        var text = TextOf(result);
        // Should include sess-1, sess-2, sess-3 (all belong to test-agent conversations)
        Assert.Contains("sess-1", text);
        Assert.Contains("sess-2", text);
        Assert.Contains("sess-3", text);
        // Should NOT include sess-4 (belongs to other-agent)
        Assert.DoesNotContain("sess-4", text);
    }

    [Fact]
    public async Task QuerySessions_FilterByStatus()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_sessions", ("status", "Sealed")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("sess-2", text);
        Assert.DoesNotContain("sess-1", text);
    }

    [Fact]
    public async Task QuerySessions_FilterByConversationId()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_sessions", ("conversation_id", "conv-1")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("sess-1", text);
        Assert.Contains("sess-2", text);
        Assert.DoesNotContain("sess-3", text);
    }

    [Fact]
    public async Task QuerySessions_FilterByDateRange()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_sessions",
            ("after", "2024-01-02T00:00:00Z"), ("before", "2024-01-03T00:00:00Z")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("sess-3", text);
        Assert.DoesNotContain("sess-1", text);
    }

    [Fact]
    public async Task QuerySessions_OtherAgentSessions_WhenExplicitAgentId()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_sessions", ("agent_id", "other-agent")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("sess-4", text);
        Assert.DoesNotContain("sess-1", text);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // query_conversations
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task QueryConversations_ReturnsOwnAgentConversations()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_conversations"));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("conv-1", text);
        Assert.Contains("conv-2", text);
        Assert.DoesNotContain("conv-3", text);
    }

    [Fact]
    public async Task QueryConversations_FilterByStatus()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_conversations", ("status", "archived")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("conv-2", text);
        Assert.DoesNotContain("conv-1", text);
    }

    [Fact]
    public async Task QueryConversations_FilterByKind()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_conversations", ("kind", "task")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("conv-2", text);
        Assert.DoesNotContain("conv-1", text);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // query_history
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task QueryHistory_RequiresSessionId()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_history"));
        Assert.True(IsError(result));
        Assert.Contains("session_id", TextOf(result));
    }

    [Fact]
    public async Task QueryHistory_ReturnsHistoryForOwnedSession()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_history", ("session_id", "sess-1")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("Hello", text);
        Assert.Contains("Hi there!", text);
    }

    [Fact]
    public async Task QueryHistory_DeniesAccessToOtherAgentSession()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_history", ("session_id", "sess-4")));
        Assert.True(IsError(result));
        Assert.Contains("not found or not owned", TextOf(result));
    }

    [Fact]
    public async Task QueryHistory_FilterByRole()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_history",
            ("session_id", "sess-1"), ("role", "user")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("Hello", text);
        Assert.Contains("How are you?", text);
        Assert.DoesNotContain("Hi there!", text);
    }

    [Fact]
    public async Task QueryHistory_Pagination()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", ArgsWithInt("query_history",
            ("session_id", "sess-1"), ("limit", 2), ("offset", 0)));
        Assert.False(IsError(result));
        var text = TextOf(result);
        // Should get first 2 entries
        Assert.Contains("Hello", text);
        Assert.Contains("Hi there!", text);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // query_sub_agents
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task QuerySubAgents_ReturnsSubAgentsForOwnedSession()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_sub_agents", ("session_id", "sess-1")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("sub-1", text);
        Assert.Contains("sub-2", text);
        Assert.Contains("coder-agent", text);
    }

    [Fact]
    public async Task QuerySubAgents_DeniesAccessToOtherAgentSession()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_sub_agents", ("session_id", "sess-4")));
        Assert.True(IsError(result));
        Assert.Contains("not found or not owned", TextOf(result));
    }

    [Fact]
    public async Task QuerySubAgents_AllOwnedSubAgents_WhenNoSessionId()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_sub_agents"));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("sub-1", text);
        Assert.Contains("sub-2", text);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // session_info
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SessionInfo_RequiresSessionId()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("session_info"));
        Assert.True(IsError(result));
        Assert.Contains("session_id", TextOf(result));
    }

    [Fact]
    public async Task SessionInfo_ReturnsInfoForOwnedSession()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("session_info", ("session_id", "sess-1")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("sess-1", text);
        Assert.Contains("conv-1", text);
        Assert.Contains("signalr", text);
    }

    [Fact]
    public async Task SessionInfo_DeniesAccessToOtherAgentSession()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("session_info", ("session_id", "sess-4")));
        Assert.True(IsError(result));
        Assert.Contains("not found or not owned", TextOf(result));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // conversation_info
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConversationInfo_RequiresConversationId()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("conversation_info"));
        Assert.True(IsError(result));
        Assert.Contains("conversation_id", TextOf(result));
    }

    [Fact]
    public async Task ConversationInfo_ReturnsInfoForOwnedConversation()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("conversation_info", ("conversation_id", "conv-1")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("conv-1", text);
        Assert.Contains("Test Conversation 1", text);
    }

    [Fact]
    public async Task ConversationInfo_DeniesAccessToOtherAgentConversation()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("conversation_info", ("conversation_id", "conv-3")));
        // Should return no results (filtered by agent_id)
        var text = TextOf(result);
        Assert.Contains("No results found", text);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // runtime_status
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RuntimeStatus_ReturnsError_WhenNoProvider()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("runtime_status"));
        Assert.True(IsError(result));
        Assert.Contains("not available", TextOf(result));
    }

    [Fact]
    public async Task RuntimeStatus_ReturnsStatus_WhenProviderAvailable()
    {
        var provider = new FakeRuntimeStateProvider("{ \"active_sessions\": 5 }");
        var tool = CreateTool(runtimeProvider: provider);
        var result = await tool.ExecuteAsync("tc1", Args("runtime_status"));
        Assert.False(IsError(result));
        Assert.Contains("active_sessions", TextOf(result));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // secret redaction (#1494)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task QueryHistory_RedactsSecretShapedValues_WhenRedactorSupplied()
    {
        // sess-1 history holds a row whose content embeds a secret token. The redactor must scrub
        // it from the serialized query output before it reaches the agent.
        const string secret = "ghp_0123456789abcdefABCDEF0123456789abcd";
        InsertHistoryRow("sess-1", 5, "assistant", $"here is the token {secret} for you");

        var redactor = new FakeSecretRedactor(secret);
        var tool = CreateTool(secretRedactor: redactor);

        var result = await tool.ExecuteAsync("tc1", Args("query_history", ("session_id", "sess-1")));

        Assert.False(IsError(result));
        Assert.True(redactor.WasInvoked);
        Assert.DoesNotContain(secret, TextOf(result));
        Assert.Contains("[REDACTED]", TextOf(result));
    }

    [Fact]
    public async Task QueryHistory_DoesNotRedact_WhenNoRedactorSupplied()
    {
        // Without a redactor the tool is a no-op pass-through (optional dependency). This pins the
        // null-safe default so a test/host that omits the redactor still gets verbatim output.
        const string secret = "ghp_0123456789abcdefABCDEF0123456789abcd";
        InsertHistoryRow("sess-1", 6, "assistant", $"token {secret} verbatim");

        var tool = CreateTool(secretRedactor: null);

        var result = await tool.ExecuteAsync("tc1", Args("query_history", ("session_id", "sess-1")));

        Assert.False(IsError(result));
        Assert.Contains(secret, TextOf(result));
    }

    [Fact]
    public async Task RawSql_RedactsSecretShapedValues_WhenRedactorSupplied()
    {
        const string secret = "sk-ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuv";
        InsertHistoryRow("sess-1", 7, "user", $"my key is {secret}");

        var redactor = new FakeSecretRedactor(secret);
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config: config, secretRedactor: redactor);

        var result = await tool.ExecuteAsync("tc1", Args("raw_sql",
            ("sql", "SELECT content FROM session_history WHERE session_id = 'sess-1'")));

        Assert.False(IsError(result));
        Assert.True(redactor.WasInvoked);
        Assert.DoesNotContain(secret, TextOf(result));
        Assert.Contains("[REDACTED]", TextOf(result));
    }

    [Fact]
    public async Task RuntimeStatus_RedactsSecretShapedValues_WhenRedactorSupplied()
    {
        const string secret = "ghp_0123456789abcdefABCDEF0123456789abcd";
        var provider = new FakeRuntimeStateProvider($"{{ \"auth_token\": \"{secret}\" }}");
        var redactor = new FakeSecretRedactor(secret);
        var tool = CreateTool(runtimeProvider: provider, secretRedactor: redactor);

        var result = await tool.ExecuteAsync("tc1", Args("runtime_status"));

        Assert.False(IsError(result));
        Assert.True(redactor.WasInvoked);
        Assert.DoesNotContain(secret, TextOf(result));
        Assert.Contains("[REDACTED]", TextOf(result));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // raw_sql
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RawSql_DisabledByDefault()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql", ("sql", "SELECT * FROM sessions")));
        Assert.True(IsError(result));
        Assert.Contains("disabled", TextOf(result));
    }

    [Fact]
    public async Task RawSql_RequiresSql()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql"));
        Assert.True(IsError(result));
        Assert.Contains("'sql' is required", TextOf(result));
    }

    [Fact]
    public async Task RawSql_ExecutesSelectQuery()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql", ("sql", "SELECT id, agent_id FROM conversations")));
        Assert.False(IsError(result));
        var text = TextOf(result);
        Assert.Contains("conv-1", text);
        Assert.Contains("test-agent", text);
    }

    [Fact]
    public async Task RawSql_AllowsWithClause()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql",
            ("sql", "WITH cte AS (SELECT id FROM conversations) SELECT * FROM cte")));
        Assert.False(IsError(result));
        Assert.Contains("conv-1", TextOf(result));
    }

    [Fact]
    public async Task RawSql_AllowsExplain()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql",
            ("sql", "EXPLAIN QUERY PLAN SELECT * FROM conversations")));
        Assert.False(IsError(result));
    }

    [Fact]
    public async Task RawSql_AllowsPragma()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql",
            ("sql", "PRAGMA table_info('conversations')")));
        Assert.False(IsError(result));
        Assert.Contains("agent_id", TextOf(result));
    }

    // ── Read-only enforcement ─────────────────────────────────────────────────

    [Theory]
    [InlineData("INSERT INTO conversations (id, agent_id, status, kind, title, created_at, updated_at) VALUES ('x','y','z','a','b','c','d')")]
    [InlineData("UPDATE conversations SET status = 'deleted'")]
    [InlineData("DELETE FROM conversations")]
    [InlineData("DROP TABLE conversations")]
    [InlineData("CREATE TABLE evil (id TEXT)")]
    [InlineData("ALTER TABLE conversations ADD COLUMN evil TEXT")]
    public async Task RawSql_RejectsNonSelectStatements(string sql)
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql", ("sql", sql)));
        Assert.True(IsError(result));
        Assert.Contains("Only SELECT statements are permitted", TextOf(result));
    }

    [Fact]
    public async Task RawSql_RejectsCommentDisguisedDml()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        // After stripping the comment, the real statement is INSERT
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql",
            ("sql", "-- this is a comment\nINSERT INTO conversations VALUES ('x','y','z','a','b','c','d')")));
        Assert.True(IsError(result));
        Assert.Contains("Only SELECT statements are permitted", TextOf(result));
    }

    [Theory]
    [InlineData("SELECT 1; PRAGMA database_list")]
    [InlineData("SELECT * FROM conversations; SELECT * FROM sessions")]
    [InlineData("SELECT id FROM conversations; DELETE FROM conversations")]
    [InlineData("PRAGMA table_info('conversations'); SELECT 1")]
    public async Task RawSql_RejectsMultipleStatements(string sql)
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql", ("sql", sql)));
        Assert.True(IsError(result));
        Assert.Contains("single statement", TextOf(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawSql_AllowsSingleStatementWithTrailingSemicolon()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql",
            ("sql", "SELECT id FROM conversations;")));
        Assert.False(IsError(result));
        Assert.Contains("conv-1", TextOf(result));
    }

    [Fact]
    public async Task RawSql_AllowsSemicolonInsideStringLiteral()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        // The ';' here is inside a quoted literal and must not be treated as a separator.
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql",
            ("sql", "SELECT 'a;b' AS v")));
        Assert.False(IsError(result));
        Assert.Contains("a;b", TextOf(result));
    }

    // ── Pagination ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pagination_RespectsLimitAndOffset()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", ArgsWithInt("query_sessions", ("limit", 1), ("offset", 0)));
        Assert.False(IsError(result));
        // Parse JSON to verify we got exactly 1 result
        var array = JsonSerializer.Deserialize<JsonElement[]>(TextOf(result));
        Assert.NotNull(array);
        Assert.Single(array);
    }

    [Fact]
    public async Task Pagination_DefaultLimitIs50()
    {
        // Just verify parsing works with no explicit limit
        var (offset, limit) = DebugTool.GetPagination(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(0, offset);
        Assert.Equal(DebugTool.DefaultLimit, limit);
    }

    [Fact]
    public async Task Pagination_LimitClampedToMax()
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["limit"] = 9999 };
        var (_, limit) = DebugTool.GetPagination(args);
        Assert.Equal(DebugTool.MaxLimit, limit);
    }

    [Fact]
    public void GetInt_JsonNumberOutOfInt32Range_DoesNotThrow_SaturatesAndClamps()
    {
        // Agent input arrives as a JsonElement. A direct GetInt32() would throw OverflowException
        // on this value BEFORE the [1, MaxLimit] clamp could bound it. GetInt must saturate instead.
        var args = JsonArgs(("limit", "99999999999")); // > int.MaxValue
        var (_, limit) = DebugTool.GetPagination(args);
        Assert.Equal(DebugTool.MaxLimit, limit); // saturated to int.MaxValue, then clamped to MaxLimit
    }

    [Fact]
    public void GetInt_JsonFractionalNumber_DoesNotThrow_TruncatesAndClamps()
    {
        // A fractional JSON number would throw FormatException from GetInt32(); truncate to int.
        var args = JsonArgs(("limit", "1.9"));
        var (_, limit) = DebugTool.GetPagination(args);
        Assert.Equal(1, limit); // 1.9 -> 1, within [1, MaxLimit]
    }

    [Fact]
    public void GetInt_JsonNegativeOutOfRange_DoesNotThrow_SaturatesAndClampsToOne()
    {
        var args = JsonArgs(("limit", "-99999999999"));
        var (_, limit) = DebugTool.GetPagination(args);
        Assert.Equal(1, limit); // saturated to int.MinValue, then clamped up to 1
    }

    [Fact]
    public void GetInt_JsonOffsetOutOfRange_DoesNotThrow_SaturatesAndFloorsAtZero()
    {
        var args = JsonArgs(("offset", "-99999999999"), ("limit", "10"));
        var (offset, _) = DebugTool.GetPagination(args);
        Assert.Equal(0, offset); // saturated to int.MinValue, then Math.Max(0, ...) floors at 0
    }

    [Fact]
    public void GetInt_JsonInRangeNumber_StillReadsExactValue()
    {
        var args = JsonArgs(("offset", "7"), ("limit", "3"));
        var (offset, limit) = DebugTool.GetPagination(args);
        Assert.Equal(7, offset);
        Assert.Equal(3, limit);
    }

    /// <summary>
    /// Builds an argument dictionary whose values are raw <see cref="JsonElement"/> numbers,
    /// mirroring how agent tool input is deserialized. The values are written as a JSON object so
    /// large/fractional numbers stay as JSON Number tokens (not boxed ints).
    /// </summary>
    private static Dictionary<string, object?> JsonArgs(params (string key, string numberLiteral)[] entries)
    {
        var json = "{" + string.Join(",", entries.Select(e => $"\"{e.key}\":{e.numberLiteral}")) + "}";
        var root = JsonDocument.Parse(json).RootElement;
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
            d[prop.Name] = prop.Value.Clone();
        return d;
    }

    // ── IsSelectOnly ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT * FROM t", true)]
    [InlineData("  SELECT * FROM t", true)]
    [InlineData("select count(*) from t", true)]
    [InlineData("WITH cte AS (SELECT 1) SELECT * FROM cte", true)]
    [InlineData("EXPLAIN SELECT 1", true)]
    [InlineData("PRAGMA table_info('t')", true)]
    [InlineData("-- comment\nSELECT 1", true)]
    [InlineData("/* block */\nSELECT 1", true)]
    [InlineData("SELECT 1;", true)]
    [InlineData("SELECT 1;   ", true)]
    [InlineData("SELECT 'a;b' AS v", true)]
    [InlineData("INSERT INTO t VALUES (1)", false)]
    [InlineData("UPDATE t SET x = 1", false)]
    [InlineData("DELETE FROM t", false)]
    [InlineData("DROP TABLE t", false)]
    [InlineData("CREATE TABLE t (id INT)", false)]
    [InlineData("SELECT 1; SELECT 2", false)]
    [InlineData("SELECT 1; DELETE FROM t", false)]
    [InlineData("PRAGMA foo; SELECT 1", false)]
    [InlineData("", false)]
    public void IsSelectOnly_CorrectlyClassifiesStatements(string sql, bool expected)
    {
        Assert.Equal(expected, DebugTool.IsSelectOnly(sql));
    }

    // ── Tool metadata ─────────────────────────────────────────────────────────

    [Fact]
    public void Name_IsPlatformDebug()
    {
        var tool = CreateTool();
        Assert.Equal("platform_debug", tool.Name);
    }

    [Fact]
    public void Definition_HasCorrectName()
    {
        var tool = CreateTool();
        Assert.Equal("platform_debug", tool.Definition.Name);
    }

    // ── Agent scope filtering ─────────────────────────────────────────────────

    [Fact]
    public async Task AgentScopeFiltering_DefaultsToCurrentAgent()
    {
        var tool = CreateTool();
        // When no agent_id is specified, should only see test-agent data
        var result = await tool.ExecuteAsync("tc1", Args("query_conversations"));
        Assert.False(IsError(result));
        Assert.DoesNotContain("other-agent", TextOf(result));
    }

    [Fact]
    public async Task AgentScopeFiltering_CanQueryOtherAgent_WhenExplicit()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query_conversations", ("agent_id", "other-agent")));
        Assert.False(IsError(result));
        Assert.Contains("conv-3", TextOf(result));
    }

    // ── ReadOnly enforcement (database-level) ─────────────────────────────────

    [Fact]
    public async Task ReadOnly_CannotWriteViaRawSql()
    {
        var config = new DebugToolConfig { AllowRawSql = true };
        var tool = CreateTool(config);
        // Even if we somehow bypass the IsSelectOnly check, the connection is read-only
        // This test verifies the conceptual guarantee is maintained via the check
        var result = await tool.ExecuteAsync("tc1", Args("raw_sql",
            ("sql", "INSERT INTO conversations VALUES ('x','y','z','a','b','c','d')")));
        Assert.True(IsError(result));
    }

    // ── Contributor tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task Contributor_ReturnsEmptyTools_WhenDisabled()
    {
        var contributor = new DebugToolContributor(_dbPath);
        var context = CreateContributorContext(extensionConfig: """{"enabled": false}""");
        var contribution = await contributor.ContributeAsync(context);
        Assert.Empty(contribution.Tools);
    }

    [Fact]
    public async Task Contributor_ReturnsDebugTool_WhenEnabled()
    {
        var contributor = new DebugToolContributor(_dbPath);
        var context = CreateContributorContext();
        var contribution = await contributor.ContributeAsync(context);
        Assert.Single(contribution.Tools);
        Assert.Equal("platform_debug", contribution.Tools[0].Name);
    }

    [Fact]
    public async Task Contributor_DefaultsToEnabled()
    {
        var contributor = new DebugToolContributor(_dbPath);
        var context = CreateContributorContext(extensionConfig: null);
        var contribution = await contributor.ContributeAsync(context);
        Assert.Single(contribution.Tools);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BotNexus.Gateway.Abstractions.Agents.AgentToolContributionContext CreateContributorContext(
        string? extensionConfig = null)
    {
        var extensionDict = new Dictionary<string, JsonElement>();
        if (extensionConfig is not null)
        {
            extensionDict["botnexus-debug-tool"] = JsonDocument.Parse(extensionConfig).RootElement.Clone();
        }

        var descriptor = new BotNexus.Gateway.Abstractions.Models.AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = extensionDict
        };

        var executionContext = new BotNexus.Gateway.Abstractions.Models.AgentExecutionContext
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("test-session")
        };

        return new BotNexus.Gateway.Abstractions.Agents.AgentToolContributionContext(
            descriptor,
            executionContext,
            Path.GetTempPath(),
            null!,
            null,
            (_, _) => Task.FromResult<string?>(null));
    }

    private sealed class FakeRuntimeStateProvider(string status) : IRuntimeStateProvider
    {
        public string GetRuntimeStatus() => status;
    }

    /// <summary>
    /// Minimal redactor that replaces every occurrence of a known token with <c>[REDACTED]</c>.
    /// Lets the redaction tests assert the data path without depending on the concrete
    /// <c>SecretRedactor</c> regex patterns.
    /// </summary>
    private sealed class FakeSecretRedactor(string secret) : BotNexus.Gateway.Abstractions.Security.ISecretRedactor
    {
        public bool WasInvoked { get; private set; }

        public string Redact(string input)
        {
            WasInvoked = true;
            return input.Replace(secret, "[REDACTED]");
        }

        public string RedactForExternalDelivery(string input) => Redact(input);
    }
}
