using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the startup migration that links orphaned sessions (those with no
/// <c>conversation_id</c>) to their agent's default conversation.
/// Runs in-process against a real SQLite database to exercise the actual migration SQL.
/// </summary>
public sealed class SqliteSessionStoreMigrationTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly string _sessionDbPath;
    private readonly string _conversationDbPath;

    public SqliteSessionStoreMigrationTests()
    {
        _directoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "SqliteSessionStoreMigrationTests",
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

    // ─── helpers ─────────────────────────────────────────────────────────────

    private SqliteConversationStore CreateConversationStore()
        => new($"Data Source={_conversationDbPath};Pooling=False",
               NullLogger<SqliteConversationStore>.Instance);

    private SqliteSessionStore CreateSessionStore(SqliteConversationStore conversationStore)
        => new($"Data Source={_sessionDbPath};Pooling=False",
               NullLogger<SqliteSessionStore>.Instance,
               conversationStore);

    /// <summary>
    /// Inserts a session row directly, bypassing the store so we can set
    /// <c>conversation_id</c> to NULL (simulating a pre-conversation-model row).
    /// Also bootstraps the pre-P9-I schema with the legacy <c>agent_id</c> column so
    /// the orphan-migration code path in <c>EnsureCreatedAsync</c> has work to find.
    /// </summary>
    private static async Task InsertOrphanedSessionAsync(
        string connectionString,
        string sessionId,
        string agentId,
        DateTimeOffset updatedAt)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        // P9-I (#674): bootstrap the legacy schema (with agent_id) before inserting.
        // The post-P9-I CREATE TABLE IF NOT EXISTS DDL has no agent_id column, so a
        // fresh table makes this INSERT fail. By creating the legacy shape here we
        // simulate the pre-P9-I-on-disk state that migration is meant to upgrade.
        await using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
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
            CREATE INDEX IF NOT EXISTS idx_sessions_agent_id ON sessions(agent_id);
            CREATE INDEX IF NOT EXISTS idx_sessions_conversation_agent ON sessions(conversation_id, agent_id);
            """;
        await schemaCmd.ExecuteNonQueryAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO sessions
                (id, agent_id, channel_type, caller_id, session_type, participants_json,
                 status, metadata, created_at, updated_at, conversation_id)
            VALUES
                ($id, $agentId, 'test', NULL, 'standard', '[]',
                 'Active', '{}', $createdAt, $updatedAt, NULL)
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$agentId", agentId);
        cmd.Parameters.AddWithValue("$createdAt", updatedAt.AddMinutes(-5).ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadConversationIdAsync(string connectionString, string sessionId)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT conversation_id FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    // ─── tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_OrphanedSessions_LinkedToDefaultConversation()
    {
        var agentId = AgentId.From("agent-orphan");

        // P9-I (#674): seed legacy-schema orphans BEFORE opening the store. Opening the
        // store now runs the post-P9-I EnsureCreatedAsync which would drop agent_id on a
        // fresh DB, leaving InsertOrphanedSessionAsync unable to write to that column.
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await InsertOrphanedSessionAsync(
            $"Data Source={_sessionDbPath};Pooling=False", "s-orphan-1", agentId.Value, t1);
        await InsertOrphanedSessionAsync(
            $"Data Source={_sessionDbPath};Pooling=False", "s-orphan-2", agentId.Value, t2);

        // Act: create a store; the migration runs during initialisation, stamps a legacy
        // conversation, then DropLegacyAgentIdColumnAsync removes the column.
        var convStore = CreateConversationStore();
        var sessionStore = CreateSessionStore(convStore);
        // Trigger init by loading a session (any operation calls EnsureCreatedAsync).
        _ = await sessionStore.GetAsync(SessionId.From("probe"), CancellationToken.None);

        // Assert: both orphaned sessions now have the legacy conversation id.
        // The migration creates a "legacy:{agentId}" conversation for orphaned sessions.
        var allConvs = await convStore.ListAsync(agentId);
        var conv = allConvs.FirstOrDefault(c => c.Title == $"legacy:{agentId.Value}");
        conv.ShouldNotBeNull("Migration should have created a legacy conversation for orphaned sessions");
        var cid1 = await ReadConversationIdAsync($"Data Source={_sessionDbPath};Pooling=False", "s-orphan-1");
        var cid2 = await ReadConversationIdAsync($"Data Source={_sessionDbPath};Pooling=False", "s-orphan-2");

        cid1.ShouldNotBeNull();
        cid2.ShouldNotBeNull();
        cid1.ShouldBe(conv!.ConversationId.Value);
        cid2.ShouldBe(conv.ConversationId.Value);

        // The most recently updated session should be the conversation's active session.
        conv.ActiveSessionId.ShouldNotBeNull();
        conv.ActiveSessionId!.Value.Value.ShouldBe("s-orphan-2");
    }

    [Fact]
    public async Task Migration_NoOrphanedSessions_IsNoOp()
    {
        var agentId = AgentId.From("agent-clean");

        // Arrange: create a session the normal way (it will have a conversation_id).
        var convStore = CreateConversationStore();
        var sessionStore = CreateSessionStore(convStore);
        var session = await sessionStore.GetOrCreateAsync(SessionId.From("s-clean-1"), agentId);
        var tempConv = await convStore.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(),
            AgentId = agentId,
            Title = "test-existing",
            IsDefault = false
        });
        session.Session.ConversationId = tempConv.ConversationId;
        await sessionStore.SaveAsync(session);

        // Remember how many conversations exist before.
        var convsBefore = await convStore.ListAsync(agentId);

        // Act: re-open — migration should be a no-op.
        var convStore2 = CreateConversationStore();
        var sessionStore2 = CreateSessionStore(convStore2);
        _ = await sessionStore2.GetAsync(SessionId.From("probe"), CancellationToken.None);

        var convsAfter = await convStore2.ListAsync(agentId);

        // No extra conversations created.
        convsAfter.Count.ShouldBe(convsBefore.Count);
    }

    [Fact]
    public async Task Migration_IsIdempotent_RunningTwiceHasNoEffect()
    {
        var agentId = AgentId.From("agent-idem");

        // P9-I (#674): seed legacy-schema orphan BEFORE opening the store (same reason as
        // Migration_OrphanedSessions_LinkedToDefaultConversation above).
        await InsertOrphanedSessionAsync(
            $"Data Source={_sessionDbPath};Pooling=False", "s-idem-1", agentId.Value,
            DateTimeOffset.UtcNow.AddMinutes(-10));

        // First migration run.
        var convStore = CreateConversationStore();
        var sessionStore = CreateSessionStore(convStore);
        _ = await sessionStore.GetAsync(SessionId.From("probe"), CancellationToken.None);

        var cidAfterFirst = await ReadConversationIdAsync(
            $"Data Source={_sessionDbPath};Pooling=False", "s-idem-1");
        var convsAfterFirst = await convStore.ListAsync(agentId);

        // Second migration run (new store instance, _initialized is false again).
        // The agent_id column was dropped by the first run, so the second run's
        // migration scan is a no-op — but we still verify post-state.
        var convStore3 = CreateConversationStore();
        var sessionStore3 = CreateSessionStore(convStore3);
        _ = await sessionStore3.GetAsync(SessionId.From("probe"), CancellationToken.None);

        var cidAfterSecond = await ReadConversationIdAsync(
            $"Data Source={_sessionDbPath};Pooling=False", "s-idem-1");
        var convsAfterSecond = await convStore3.ListAsync(agentId);

        // Same conversation id, no duplicate default conversations.
        cidAfterSecond.ShouldBe(cidAfterFirst);
        convsAfterSecond.Count.ShouldBe(convsAfterFirst.Count);
    }
}
