using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Regression tests for <see cref="SqliteSessionStore"/> ConversationId persistence.
/// </summary>
public sealed class SqliteSessionStoreConversationIdTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteSessionStoreConversationIdTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-tests-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private SqliteSessionStore CreateStore()
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance);

    [Fact]
    public async Task SaveAsync_WithConversationId_PersistedAndReloadedAfterStoreRebuild()
    {
        var sessionId = SessionId.From("test-session-persist-conv");
        var agentId = AgentId.From("agent-persist-test");
        var conversationId = ConversationId.Create();

        // Save a session with ConversationId stamped
        var store1 = CreateStore();
        var session = await store1.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversationId;
        await store1.SaveAsync(session);

        // Create a brand-new store (no cache) pointing at the same DB
        var store2 = CreateStore();
        var reloaded = await store2.GetAsync(sessionId);

        reloaded.ShouldNotBeNull();
        reloaded!.Session.ConversationId.ShouldNotBeNull();
        reloaded.Session.ConversationId!.Value.ShouldBe(conversationId);
    }

    [Fact]
    public async Task SaveAsync_NullConversationId_LoadsAsNull()
    {
        var sessionId = SessionId.From("test-session-no-conv");
        var agentId = AgentId.From("agent-no-conv");

        var store1 = CreateStore();
        var session = await store1.GetOrCreateAsync(sessionId, agentId);
        // ConversationId intentionally left null
        await store1.SaveAsync(session);

        var store2 = CreateStore();
        var reloaded = await store2.GetAsync(sessionId);

        reloaded.ShouldNotBeNull();
        reloaded!.Session.ConversationId.ShouldBeNull();
    }

    [Fact]
    public async Task EnumerateSessions_IncludesConversationId()
    {
        var sessionId = SessionId.From("test-session-enumerate-conv");
        var agentId = AgentId.From("agent-enumerate");
        var conversationId = ConversationId.Create();

        var store1 = CreateStore();
        var session = await store1.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversationId;
        await store1.SaveAsync(session);

        var store2 = CreateStore();
        var all = await store2.ListAsync();
        var found = all.FirstOrDefault(s => s.SessionId == sessionId);

        found.ShouldNotBeNull();
        found!.Session.ConversationId.ShouldNotBeNull();
        found.Session.ConversationId!.Value.ShouldBe(conversationId);
    }
}
