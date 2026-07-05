using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Issue #1518 regression tests: the post-run finalizer must not resurrect a session that was
/// deleted mid-run, nor clobber a session that was sealed/rebound by a competing reset while the
/// run was in flight. These exercise the fenced
/// <see cref="ISessionStore.SaveAsync(GatewaySession, SessionWriteFence, System.Threading.CancellationToken)"/>
/// overload directly against <see cref="SqliteSessionStore"/> (the production store, whose
/// unconditional <c>INSERT ... ON CONFLICT DO UPDATE</c> upsert is the resurrection vector) so the
/// gap is proven closed at the store layer that every finalizer path funnels through.
/// </summary>
public sealed class SqliteSessionStorePostRunFenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly InMemoryConversationStore _conversations = new();

    public SqliteSessionStorePostRunFenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-fence-tests-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
    }

    private SqliteSessionStore CreateStore()
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private async Task<(SqliteSessionStore Store, GatewaySession Session, SessionWriteFence Fence)> ArrangeSavedSessionAsync(
        string sessionIdValue,
        string agentIdValue,
        ConversationId conversationId)
    {
        var agentId = AgentId.From(agentIdValue);
        // P9-I (#674): the conversation must exist before AgentId hydration runs on reload.
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId
        });

        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From(sessionIdValue), agentId);
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "first" });
        await store.SaveAsync(session);

        // Capture the run identity exactly as the gateway does at run start.
        var fence = SessionWriteFence.Capture(session);
        return (store, session, fence);
    }

    [Fact]
    public async Task FencedSave_AfterDeleteMidRun_DoesNotResurrectRow()
    {
        var conversationId = ConversationId.Create();
        var (store, session, fence) = await ArrangeSavedSessionAsync("s-delete", "agent-a", conversationId);

        // A competing delete lands while the "run" is in flight.
        await store.DeleteAsync(session.SessionId);
        (await store.GetAsync(session.SessionId)).ShouldBeNull("precondition: the row is deleted");

        // The finalizer now tries to persist the completed turn against the captured identity.
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "late-turn" });
        var outcome = await store.SaveAsync(session, fence);

        outcome.ShouldBe(SessionSaveOutcome.Rebound, "a delete mid-run must short-circuit the finalizer write");

        // The row must NOT have been resurrected - not in this store, and not via a cold store.
        (await store.GetAsync(session.SessionId)).ShouldBeNull("the deleted session must stay deleted");
        (await CreateStore().GetAsync(session.SessionId)).ShouldBeNull("cold read must also see no resurrected row");
    }

    [Fact]
    public async Task FencedSave_AfterSealByResetMidRun_DoesNotUnseal()
    {
        var conversationId = ConversationId.Create();
        var (store, session, fence) = await ArrangeSavedSessionAsync("s-seal", "agent-b", conversationId);

        // A competing reset seals the row (DefaultConversationResetService step 4) mid-run.
        // Seal through a SEPARATE cold store so the run's in-memory `session` (served from the
        // first store's cache) is not mutated by reference - modelling two independent actors.
        var resetStore = CreateStore();
        var sealer = await resetStore.GetAsync(session.SessionId);
        sealer.ShouldNotBeNull();
        sealer!.Status = SessionStatus.Sealed;
        await resetStore.SaveAsync(sealer);

        // The in-memory finalizer still believes the session is Active and appends its turn.
        session.Status.ShouldBe(SessionStatus.Active, "precondition: the run's copy is still Active");
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "late-turn" });
        var outcome = await store.SaveAsync(session, fence);

        outcome.ShouldBe(SessionSaveOutcome.Rebound, "a seal-by-reset mid-run must short-circuit the finalizer write");

        // The persisted row must remain Sealed - the finalizer must not revert it to Active.
        var reloaded = await CreateStore().GetAsync(session.SessionId);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(SessionStatus.Sealed, "the finalizer must not un-seal a reset session");
        reloaded.GetHistorySnapshot().ShouldNotContain(e => e.Content == "late-turn", "the late turn must not be persisted");
    }

    [Fact]
    public async Task FencedSave_AfterConversationRebindMidRun_DoesNotClobber()
    {
        var originalConversationId = ConversationId.Create();
        var (store, session, fence) = await ArrangeSavedSessionAsync("s-rebind", "agent-c", originalConversationId);

        // A competing path rebinds the same session id to a different conversation mid-run.
        var rebindConversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = rebindConversationId,
            AgentId = session.AgentId
        });
        var rebinder = await CreateStore().GetAsync(session.SessionId);
        rebinder.ShouldNotBeNull();
        rebinder!.Session.ConversationId = rebindConversationId;
        await CreateStore().SaveAsync(rebinder);

        // The finalizer still holds the ORIGINAL conversation identity.
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "late-turn" });
        var outcome = await store.SaveAsync(session, fence);

        outcome.ShouldBe(SessionSaveOutcome.Rebound, "a conversation rebind mid-run must short-circuit the finalizer write");

        var reloaded = await CreateStore().GetAsync(session.SessionId);
        reloaded.ShouldNotBeNull();
        reloaded!.Session.ConversationId.ShouldBe(rebindConversationId, "the fresh binding must survive; the finalizer must not clobber it");
    }

    [Fact]
    public async Task FencedSave_WhenSessionUnchanged_PersistsNormally()
    {
        var conversationId = ConversationId.Create();
        var (store, session, fence) = await ArrangeSavedSessionAsync("s-ok", "agent-d", conversationId);

        // Normal happy path: nothing deleted or reset; the finalizer's turn must land.
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "clean-turn" });
        var outcome = await store.SaveAsync(session, fence);

        outcome.ShouldBe(SessionSaveOutcome.Persisted, "an unchanged session must persist the completed turn");

        var reloaded = await CreateStore().GetAsync(session.SessionId);
        reloaded.ShouldNotBeNull();
        reloaded!.GetHistorySnapshot().ShouldContain(e => e.Content == "clean-turn", "the completed turn must be persisted");
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
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite file locks can linger briefly on Windows.
        }
    }
}
