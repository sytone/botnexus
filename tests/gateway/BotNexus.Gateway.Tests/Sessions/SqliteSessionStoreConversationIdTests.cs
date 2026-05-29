using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
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
    private readonly InMemoryConversationStore _conversations = new();

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
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.Delete(_dbPath);
                        return;
                    }
                    catch (IOException)
                    {
                        if (attempt >= 4)
                            break;
                        Thread.Sleep(50);
                    }
                }

                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // Cleanup best effort; locking can linger briefly after async DB operations on Windows.
        }
    }

    private SqliteSessionStore CreateStore(IConversationStore? conversationStore = null)
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, conversationStore ?? _conversations);

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
        reloaded!.Session.ConversationId.IsInitialized().ShouldBeTrue();
        reloaded.Session.ConversationId.ShouldBe(conversationId);
    }

    [Fact]
    public async Task SaveAsync_UnsetConversationId_BackfillsLegacyOnRoundTrip()
    {
        // Phase 9 / P9-B-2 (#627): Session.ConversationId is non-nullable. Unset values
        // are stamped by the legacy resolver on save and the UPDATE in BackfillLoadedSessionAsync
        // makes the row indexed-queryable. Round-trips as a real ConversationId.
        var sessionId = SessionId.From("test-session-no-conv");
        var agentId = AgentId.From("agent-no-conv");

        var store1 = CreateStore();
        var session = await store1.GetOrCreateAsync(sessionId, agentId);
        await store1.SaveAsync(session);

        var store2 = CreateStore();
        var reloaded = await store2.GetAsync(sessionId);

        reloaded.ShouldNotBeNull();
        reloaded!.Session.ConversationId.IsInitialized().ShouldBeTrue(
            "Unset ConversationId must be backfilled by the legacy resolver on save (#627).");
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
        found!.Session.ConversationId.IsInitialized().ShouldBeTrue();
        found.Session.ConversationId.ShouldBe(conversationId);
    }
}
