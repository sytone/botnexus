using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>Pins the fenced, sentinel-only delete behind <see cref="SqliteSessionStore.TryDeleteRedundantCrashSentinelAsync"/>.</summary>
public sealed class SqliteSessionStoreRedundantCrashSentinelTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-redundant-sentinel-{Guid.NewGuid():N}.db");
    private readonly InMemoryConversationStore _conversations = new();
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreRedundantCrashSentinelTests()
        => _store = new SqliteSessionStore(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString();

    [Fact]
    public async Task TryDeleteRedundantCrashSentinelAsync_AnotherSentinelRemains_DeletesOnlyTheKeyedRow()
    {
        var (session, user, first, second) = await SaveSessionAsync();
        var other = (await SaveSessionAsync("session-2")).Session;
        (await _store.GetAsync(session.SessionId)).ShouldNotBeNull("a read before the delete caches the aggregate");

        (await _store.TryDeleteRedundantCrashSentinelAsync(SessionWriteFence.Capture(session), KeyOf(second))).ShouldBeTrue();

        (await _store.GetAsync(other.SessionId)).ShouldNotBeNull().History.Count(entry => entry.IsCrashSentinel)
            .ShouldBe(2, "a row with the same key in another session is untouched");
        var stored = (await _store.GetAsync(session.SessionId)).ShouldNotBeNull().History;
        stored.Where(entry => entry.IsCrashSentinel).Select(entry => entry.PersistenceKey).ShouldBe([KeyOf(first)]);
        stored.ShouldContain(entry => entry.PersistenceKey == KeyOf(user), "ordinary rows are untouched");
        (await _store.TryDeleteRedundantCrashSentinelAsync(SessionWriteFence.Capture(session), KeyOf(first)))
            .ShouldBeFalse("the last stored sentinel still protects the session and must be kept");
    }

    [Fact]
    public async Task TryDeleteRedundantCrashSentinelAsync_KeyOfOrdinaryRow_DeletesNothing()
    {
        var (session, user, _, _) = await SaveSessionAsync();

        (await _store.TryDeleteRedundantCrashSentinelAsync(SessionWriteFence.Capture(session), KeyOf(user))).ShouldBeFalse();

        (await _store.GetAsync(session.SessionId)).ShouldNotBeNull().History.Count.ShouldBe(3);
    }

    [Fact]
    public async Task TryDeleteRedundantCrashSentinelAsync_SessionSealed_DeletesNothing()
    {
        var (session, _, _, second) = await SaveSessionAsync();
        var fence = SessionWriteFence.Capture(session);
        session.Status = SessionStatus.Sealed;
        await _store.SaveAsync(session);

        (await _store.TryDeleteRedundantCrashSentinelAsync(fence, KeyOf(second))).ShouldBeFalse();

        (await _store.GetAsync(session.SessionId)).ShouldNotBeNull().History.Count(entry => entry.IsCrashSentinel).ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryDeleteRedundantCrashSentinelAsync_SessionDeletedOrRebound_DeletesNothing(bool deleted)
    {
        var (session, _, _, second) = await SaveSessionAsync();
        var fence = SessionWriteFence.Capture(session);
        if (deleted)
            await _store.DeleteAsync(session.SessionId);
        else
            fence = fence with { ExpectedConversationId = ConversationId.Create() };

        (await _store.TryDeleteRedundantCrashSentinelAsync(fence, KeyOf(second))).ShouldBeFalse();

        var stored = await _store.GetAsync(session.SessionId);
        if (deleted)
            stored.ShouldBeNull("the refused delete must not recreate the session");
        else
            stored.ShouldNotBeNull().History.Count(entry => entry.IsCrashSentinel).ShouldBe(2);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
        try { File.Delete(_dbPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A lingering file lock must not fail the run. */ }
    }

    private static string KeyOf(SessionEntry entry) => entry.PersistenceKey.ShouldNotBeNull("a saved row carries its persistence key");

    private async Task<(GatewaySession Session, SessionEntry User, SessionEntry First, SessionEntry Second)> SaveSessionAsync(
        string id = "session-1")
    {
        // Fixed keys, so another session can hold rows with the same keys.
        var session = await _store.GetOrCreateAsync(SessionId.From(id), AgentId.From("agent-a"));
        session.ConversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation { ConversationId = session.ConversationId, AgentId = session.AgentId });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello", PersistenceKey = "user-row" });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "turn sentinel", IsCrashSentinel = true, PersistenceKey = "turn-sentinel" });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "follow-up sentinel", IsCrashSentinel = true, PersistenceKey = "follow-up-sentinel" });
        await _store.SaveAsync(session);
        var history = session.GetHistorySnapshot();
        return (session, history[0], history[1], history[2]);
    }
}
