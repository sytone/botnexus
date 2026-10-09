using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SqliteSessionStoreAtomicSaveTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly InMemoryConversationStore _conversations = new();

    public SqliteSessionStoreAtomicSaveTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-atomic-save-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
    }

    private SqliteSessionStore CreateStore()
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private async Task<(SqliteSessionStore Store, GatewaySession Session)> ArrangeSavedSessionAsync(string id)
    {
        var agentId = AgentId.From("atomic-save-agent");
        var conversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId
        });

        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From(id), agentId);
        session.ConversationId = conversationId;
        session.Metadata["version"] = "before";
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "seed" });
        await store.SaveAsync(session);
        return (store, session);
    }

    [Fact]
    public async Task SaveAsync_WhenHistoryPersistenceFails_RollsBackSessionMetadata()
    {
        var (store, session) = await ArrangeSavedSessionAsync("ordinary-failure");
        session.Metadata["version"] = "after";
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "must-not-commit" });
        store.BeforeHistoryCommitAsync = (_, _) => throw new InvalidOperationException("injected history failure");

        await Should.ThrowAsync<InvalidOperationException>(() => store.SaveAsync(session));

        var reloaded = await CreateStore().GetAsync(session.SessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Metadata["version"]?.ToString().ShouldBe("before");
        reloaded.GetHistorySnapshot().Select(entry => entry.Content).ShouldBe(["seed"]);
    }

    [Fact]
    public async Task FencedSaveAsync_WhenHistoryPersistenceIsCancelled_RollsBackSessionMetadata()
    {
        var (store, session) = await ArrangeSavedSessionAsync("fenced-cancellation");
        var fence = SessionWriteFence.Capture(session);
        session.Metadata["version"] = "after";
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "must-not-commit" });
        using var cancellation = new CancellationTokenSource();
        store.BeforeHistoryCommitAsync = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };

        await Should.ThrowAsync<OperationCanceledException>(() => store.SaveAsync(session, fence, cancellation.Token));

        var reloaded = await CreateStore().GetAsync(session.SessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Metadata["version"]?.ToString().ShouldBe("before");
        reloaded.GetHistorySnapshot().Select(entry => entry.Content).ShouldBe(["seed"]);
    }

    [Fact]
    public async Task SaveAsync_WhenReconciliationFailsAfterMutations_RollsBackAndRetryConverges()
    {
        var (store, session) = await ArrangeSavedSessionAsync("reconciliation-failure");
        session.Metadata["version"] = "after";
        session.ReplaceHistory([new SessionEntry { Role = MessageRole.Assistant, Content = "replacement" }]);
        store.BeforeHistoryCommitAsync = (_, _) => throw new InvalidOperationException("injected reconciliation failure");

        await Should.ThrowAsync<InvalidOperationException>(() => store.SaveAsync(session));

        var afterFailure = await CreateStore().GetAsync(session.SessionId);
        afterFailure.ShouldNotBeNull();
        afterFailure.Metadata["version"]?.ToString().ShouldBe("before");
        afterFailure.GetHistorySnapshot().Select(entry => entry.Content).ShouldBe(["seed"]);

        store.BeforeHistoryCommitAsync = null;
        await store.SaveAsync(session);

        var afterRetry = await CreateStore().GetAsync(session.SessionId);
        afterRetry.ShouldNotBeNull();
        afterRetry.Metadata["version"]?.ToString().ShouldBe("after");
        afterRetry.GetHistorySnapshot().Select(entry => entry.Content).ShouldBe(["replacement"]);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(_dbPath);
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite file locks can linger briefly on Windows.
        }
    }
}
