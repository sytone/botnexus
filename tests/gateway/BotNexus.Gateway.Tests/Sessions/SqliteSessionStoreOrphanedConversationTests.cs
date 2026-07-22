using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Regression tests for issue #2188: a single session row whose non-null
/// <c>conversation_id</c> references a deleted conversation must not crash gateway
/// startup. Covers (1) startup self-heal of dangling references, (2) resilient
/// enumeration that skips-and-logs unrecoverable rows, and (3) the cron startup
/// reconciler tolerating such rows without aborting the host.
/// </summary>
public sealed class SqliteSessionStoreOrphanedConversationTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly string _sessionDbPath;
    private readonly string _conversationDbPath;

    public SqliteSessionStoreOrphanedConversationTests()
    {
        _directoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "SqliteSessionStoreOrphanedConversationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        _sessionDbPath = Path.Combine(_directoryPath, "sessions.db");
        _conversationDbPath = Path.Combine(_directoryPath, "conversations.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directoryPath))
        {
            try { Directory.Delete(_directoryPath, recursive: true); }
            catch (IOException) { /* best effort; Windows may linger locks briefly */ }
        }
    }

    private string SessionConn => $"Data Source={_sessionDbPath};Pooling=False";

    private SqliteConversationStore CreateConversationStore()
        => new($"Data Source={_conversationDbPath};Pooling=False",
               NullLogger<SqliteConversationStore>.Instance);

    private SqliteSessionStore CreateSessionStore(SqliteConversationStore conversationStore)
        => new(SessionConn, NullLogger<SqliteSessionStore>.Instance, conversationStore);

    /// <summary>
    /// Inserts a post-P9-I session row directly (bypassing the store), setting a
    /// non-null <c>conversation_id</c> that has no matching conversation - the exact
    /// dangling-reference shape that used to be fatal.
    /// </summary>
    private static async Task InsertDanglingSessionAsync(
        string connectionString,
        string sessionId,
        string conversationId,
        string channelType = "test")
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO sessions
                (id, channel_type, caller_id, session_type, participants_json,
                 status, metadata, created_at, updated_at, conversation_id)
            VALUES
                ($id, $channel, NULL, 'standard', '[]',
                 'Active', '{}', $createdAt, $updatedAt, $conversationId)
            """;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$channel", channelType);
        cmd.Parameters.AddWithValue("$createdAt", now.AddMinutes(-5).ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        cmd.Parameters.AddWithValue("$conversationId", conversationId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> SessionRowExistsAsync(string connectionString, string sessionId)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        return count > 0;
    }

    // ─── sad path: dangling reference must be healed on startup ───────────────

    [Fact]
    public async Task Startup_DanglingConversationReference_IsSelfHealed()
    {
        // Arrange: open a store once so the post-P9-I schema exists, then insert a
        // raw session row referencing a conversation id that was never created.
        var convStore1 = CreateConversationStore();
        var store1 = CreateSessionStore(convStore1);
        _ = await store1.GetAsync(SessionId.From("probe"), CancellationToken.None);

        await InsertDanglingSessionAsync(SessionConn, "s-dangling", ConversationId.Create().Value);

        (await SessionRowExistsAsync(SessionConn, "s-dangling")).ShouldBeTrue();

        // Act: a brand-new store re-runs startup migration, which must quarantine/remove
        // the dangling row instead of leaving it to crash later loads.
        var convStore2 = CreateConversationStore();
        var store2 = CreateSessionStore(convStore2);
        _ = await store2.GetAsync(SessionId.From("probe"), CancellationToken.None);

        // Assert: the unrecoverable row is gone (self-healed).
        (await SessionRowExistsAsync(SessionConn, "s-dangling")).ShouldBeFalse();
    }

    // ─── sad path: enumeration must skip unrecoverable rows, not throw ─────────

    [Fact]
    public async Task ListAsync_WithUnrecoverableRow_SkipsAndReturnsHealthyRows()
    {
        // Arrange: one healthy session (valid conversation) and one dangling row.
        var convStore = CreateConversationStore();
        var store = CreateSessionStore(convStore);

        var agentId = AgentId.From("agent-heal");
        var conversation = await convStore.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId
        });
        var healthy = await store.GetOrCreateAsync(SessionId.From("s-healthy"), agentId);
        healthy.Session.ConversationId = conversation.ConversationId;
        await store.SaveAsync(healthy);

        // Inject a dangling row directly and query through a fresh store so its cache
        // is cold and it must hydrate the bad row from disk. The dangling row survives
        // because we bypass the startup heal by writing it after init on the same store.
        await InsertDanglingSessionAsync(SessionConn, "s-bad", ConversationId.Create().Value);

        // Act + Assert: enumeration must not throw; it skips the bad row and returns
        // the healthy one.
        var sessions = await store.ListAsync(null, CancellationToken.None);

        sessions.ShouldContain(s => s.SessionId == SessionId.From("s-healthy"));
        sessions.ShouldNotContain(s => s.SessionId == SessionId.From("s-bad"));
    }

    // ─── sad path: reconciler must never abort host startup ───────────────────

    [Fact]
    public async Task Reconciler_StartingAsync_WithUnrecoverableRow_DoesNotThrow()
    {
        var convStore = CreateConversationStore();
        var store = CreateSessionStore(convStore);
        _ = await store.GetAsync(SessionId.From("probe"), CancellationToken.None);

        // A dangling cron row that used to propagate a fatal exception out of
        // Host.StartAsync via CronSessionStartupReconciler.StartingAsync.
        await InsertDanglingSessionAsync(SessionConn, "cron:dangling", ConversationId.Create().Value, "cron");

        var reconciler = new CronSessionStartupReconciler(
            store, NullLogger<CronSessionStartupReconciler>.Instance);

        // Must complete without throwing.
        await Should.NotThrowAsync(() => reconciler.StartingAsync(CancellationToken.None));
    }
}
