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
/// Issue #2132 regression tests: session read-modify-write operations must not overwrite each
/// other. The ordinary <see cref="ISessionStore.SaveAsync(GatewaySession, CancellationToken)"/>
/// writes the whole aggregate and replaces the complete history, so a controller that reads a
/// session, edits metadata, and saves will silently discard any transcript entry appended in the
/// gap - and vice versa. The narrow mutation APIs
/// (<see cref="ISessionStore.AppendEntriesAsync"/>, <see cref="ISessionStore.PatchMetadataAsync"/>,
/// <see cref="ISessionStore.TransitionStatusAsync"/>) write disjoint state under the store's
/// per-session lock and never rebuild the aggregate from a stale snapshot.
/// </summary>
/// <remarks>
/// The interleavings here are forced with <see cref="TaskCompletionSource"/> gates, never with
/// timing sleeps: each actor signals when its snapshot is captured and waits for the other before
/// writing, so the "both read stale, then both write" ordering is deterministic on every run.
/// Two <b>separate</b> <see cref="SqliteSessionStore"/> instances share one database file so the
/// two actors genuinely hold independent snapshots (a single instance would hand both actors the
/// same cached object and hide the defect).
/// </remarks>
public sealed class SqliteSessionStoreAtomicMutationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly InMemoryConversationStore _conversations = new();

    public SqliteSessionStoreAtomicMutationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-atomic-session-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
    }

    private SqliteSessionStore CreateStore()
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    /// <summary>
    /// Seeds one persisted session with a single transcript entry and one metadata key so every
    /// test starts from an authoritative row rather than a warm cache entry.
    /// </summary>
    private async Task<SessionId> ArrangeSessionAsync(string sessionIdValue, string agentIdValue)
    {
        var agentId = AgentId.From(agentIdValue);
        var conversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId
        });

        var store = CreateStore();
        var sessionId = SessionId.From(sessionIdValue);
        var session = await store.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "seed" });
        session.Metadata["seedKey"] = "seedValue";
        await store.SaveAsync(session);
        return sessionId;
    }

    /// <summary>
    /// Runs two actors so that BOTH capture their snapshot before EITHER writes, then releases
    /// the writes in a fixed order. Purely gate-driven - no sleeps, no polling.
    /// </summary>
    private static async Task RunInterleavedAsync(
        Func<Task> firstRead,
        Func<Task> secondRead,
        Func<Task> firstWrite,
        Func<Task> secondWrite)
    {
        var firstReadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWriteDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstActor = Task.Run(async () =>
        {
            await firstRead();
            firstReadDone.SetResult();
            // Do not write until the other actor's snapshot is also stale-by-construction.
            await secondReadDone.Task;
            await firstWrite();
            firstWriteDone.SetResult();
        });

        var secondActor = Task.Run(async () =>
        {
            await secondRead();
            secondReadDone.SetResult();
            await firstReadDone.Task;
            // Write strictly after the first actor's write has committed.
            await firstWriteDone.Task;
            await secondWrite();
        });

        await Task.WhenAll(firstActor, secondActor);
    }

    [Fact]
    public async Task TranscriptAppendAndMetadataPatch_AcrossTwoStores_BothSurvive()
    {
        var sessionId = await ArrangeSessionAsync("s-append-vs-metadata", "agent-a");

        // Two independent stores over ONE database => two genuinely independent snapshots.
        var appendStore = CreateStore();
        var metadataStore = CreateStore();

        GatewaySession? appendSnapshot = null;
        GatewaySession? metadataSnapshot = null;

        await RunInterleavedAsync(
            firstRead: async () => appendSnapshot = await appendStore.GetAsync(sessionId),
            secondRead: async () => metadataSnapshot = await metadataStore.GetAsync(sessionId),
            firstWrite: async () => await appendStore.AppendEntriesAsync(
                sessionId,
                [new SessionEntry { Role = MessageRole.Assistant, Content = "appended-turn" }]),
            // The metadata actor's snapshot predates the append; a whole-aggregate save would
            // replace history with the stale one-entry transcript.
            secondWrite: async () => await metadataStore.PatchMetadataAsync(
                sessionId,
                new Dictionary<string, object?> { ["patchedKey"] = "patchedValue" }));

        appendSnapshot.ShouldNotBeNull();
        metadataSnapshot.ShouldNotBeNull();

        // Cold read of the authoritative row: both mutations must be present.
        var reloaded = await CreateStore().GetAsync(sessionId);
        reloaded.ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Select(e => e.Content)
            .ShouldBe(["seed", "appended-turn"], "the transcript append must survive the metadata patch");
        reloaded.Metadata["seedKey"]?.ToString().ShouldBe("seedValue");
        reloaded.Metadata["patchedKey"]?.ToString().ShouldBe("patchedValue", "the metadata patch must survive the transcript append");
    }

    [Fact]
    public async Task MetadataPatchThenTranscriptAppend_AcrossTwoStores_BothSurvive()
    {
        var sessionId = await ArrangeSessionAsync("s-metadata-vs-append", "agent-b");

        var metadataStore = CreateStore();
        var appendStore = CreateStore();

        await RunInterleavedAsync(
            firstRead: async () => await metadataStore.GetAsync(sessionId),
            secondRead: async () => await appendStore.GetAsync(sessionId),
            firstWrite: async () => await metadataStore.PatchMetadataAsync(
                sessionId,
                new Dictionary<string, object?> { ["patchedKey"] = "patchedValue" }),
            // The append actor's snapshot predates the metadata patch.
            secondWrite: async () => await appendStore.AppendEntriesAsync(
                sessionId,
                [new SessionEntry { Role = MessageRole.Assistant, Content = "appended-turn" }]));

        var reloaded = await CreateStore().GetAsync(sessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Metadata["patchedKey"]?.ToString().ShouldBe("patchedValue", "the metadata patch must not be clobbered by the later append");
        reloaded.GetHistorySnapshot().Select(e => e.Content).ShouldBe(["seed", "appended-turn"]);
    }

    [Fact]
    public async Task PatchMetadata_NullValue_RemovesKeyWithoutTouchingTranscript()
    {
        var sessionId = await ArrangeSessionAsync("s-metadata-remove", "agent-c");
        var store = CreateStore();

        var result = await store.PatchMetadataAsync(
            sessionId,
            new Dictionary<string, object?> { ["seedKey"] = null, ["kept"] = "yes" });

        result.Outcome.ShouldBe(SessionMutationOutcome.Applied);
        result.Metadata.ShouldNotContainKey("seedKey");
        result.Metadata["kept"]?.ToString().ShouldBe("yes");

        var reloaded = await CreateStore().GetAsync(sessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Metadata.ShouldNotContainKey("seedKey");
        reloaded.Metadata["kept"]?.ToString().ShouldBe("yes");
        reloaded.GetHistorySnapshot().Count.ShouldBe(1, "a metadata patch must never rewrite the transcript");
    }

    [Fact]
    public async Task TranscriptAppendAndLifecycleSuspend_AcrossTwoStores_BothSurvive()
    {
        var sessionId = await ArrangeSessionAsync("s-append-vs-suspend", "agent-d");

        var appendStore = CreateStore();
        var lifecycleStore = CreateStore();

        await RunInterleavedAsync(
            firstRead: async () => await appendStore.GetAsync(sessionId),
            secondRead: async () => await lifecycleStore.GetAsync(sessionId),
            firstWrite: async () => await appendStore.AppendEntriesAsync(
                sessionId,
                [new SessionEntry { Role = MessageRole.Assistant, Content = "appended-turn" }]),
            secondWrite: async () =>
            {
                var transition = await lifecycleStore.TransitionStatusAsync(
                    sessionId,
                    [SessionStatus.Active],
                    SessionStatus.Suspended);
                transition.Outcome.ShouldBe(SessionMutationOutcome.Applied);
            });

        var reloaded = await CreateStore().GetAsync(sessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Status.ShouldBe(SessionStatus.Suspended, "a valid lifecycle transition must survive a concurrent append");
        reloaded.GetHistorySnapshot().Select(e => e.Content)
            .ShouldBe(["seed", "appended-turn"], "the append must survive the lifecycle transition");
    }

    [Fact]
    public async Task TransitionStatus_FromUnexpectedState_ReturnsExplicitConflict()
    {
        var sessionId = await ArrangeSessionAsync("s-transition-conflict", "agent-e");

        // Actor 1 suspends the session. Actor 2 holds a snapshot that still says Active and
        // tries to suspend it too - a compare-and-set on the persisted status must reject it.
        var first = CreateStore();
        var second = CreateStore();

        var snapshot = await second.GetAsync(sessionId);
        snapshot.ShouldNotBeNull();
        snapshot.Status.ShouldBe(SessionStatus.Active, "precondition: the stale snapshot believes the session is Active");

        (await first.TransitionStatusAsync(sessionId, [SessionStatus.Active], SessionStatus.Suspended))
            .Outcome.ShouldBe(SessionMutationOutcome.Applied);

        var stale = await second.TransitionStatusAsync(sessionId, [SessionStatus.Active], SessionStatus.Suspended);
        stale.Outcome.ShouldBe(SessionMutationOutcome.Conflict, "the CAS must reject a transition computed from a stale status");
        stale.Status.ShouldBe(SessionStatus.Suspended, "the conflict must report the authoritative current status");

        var reloaded = await CreateStore().GetAsync(sessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Status.ShouldBe(SessionStatus.Suspended);
    }

    [Fact]
    public async Task AppendEntries_ToSealedSession_ReturnsExplicitConflict()
    {
        var sessionId = await ArrangeSessionAsync("s-append-sealed", "agent-f");

        var sealer = CreateStore();
        (await sealer.TransitionStatusAsync(sessionId, [SessionStatus.Active, SessionStatus.Suspended], SessionStatus.Sealed))
            .Outcome.ShouldBe(SessionMutationOutcome.Applied);

        // A writer holding a pre-seal snapshot must be refused, not silently un-seal the row.
        var late = CreateStore();
        var result = await late.AppendEntriesAsync(
            sessionId,
            [new SessionEntry { Role = MessageRole.Assistant, Content = "too-late" }]);

        result.Outcome.ShouldBe(SessionMutationOutcome.Conflict, "appending to a sealed session must be an explicit conflict");

        var reloaded = await CreateStore().GetAsync(sessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Status.ShouldBe(SessionStatus.Sealed, "a refused append must not revert the sealed status");
        reloaded.GetHistorySnapshot().Count.ShouldBe(1, "a refused append must not write history");
    }

    [Fact]
    public async Task Mutations_OnMissingSession_ReturnNotFoundAndCreateNoRow()
    {
        var store = CreateStore();
        var missing = SessionId.From("s-does-not-exist");

        (await store.AppendEntriesAsync(missing, [new SessionEntry { Role = MessageRole.User, Content = "x" }]))
            .Outcome.ShouldBe(SessionMutationOutcome.NotFound);
        (await store.PatchMetadataAsync(missing, new Dictionary<string, object?> { ["k"] = "v" }))
            .Outcome.ShouldBe(SessionMutationOutcome.NotFound);
        (await store.TransitionStatusAsync(missing, [SessionStatus.Active], SessionStatus.Suspended))
            .Outcome.ShouldBe(SessionMutationOutcome.NotFound);

        (await CreateStore().GetAsync(missing)).ShouldBeNull("a mutation must never resurrect or create a session row");
    }

    [Fact]
    public async Task SameInstance_SharedObjectRace_AppendAndMetadataBothSurvive()
    {
        // Same-instance case: BOTH actors are served the SAME cached GatewaySession object by one
        // store, so a read-modify-write pair also races on shared mutable state, not just on the
        // database row. The narrow mutations must still be individually durable.
        var sessionId = await ArrangeSessionAsync("s-shared-object", "agent-g");
        var store = CreateStore();

        GatewaySession? left = null;
        GatewaySession? right = null;

        await RunInterleavedAsync(
            firstRead: async () => left = await store.GetAsync(sessionId),
            secondRead: async () => right = await store.GetAsync(sessionId),
            firstWrite: async () => await store.AppendEntriesAsync(
                sessionId,
                [new SessionEntry { Role = MessageRole.Assistant, Content = "shared-append" }]),
            secondWrite: async () => await store.PatchMetadataAsync(
                sessionId,
                new Dictionary<string, object?> { ["sharedKey"] = "sharedValue" }));

        left.ShouldNotBeNull();
        right.ShouldNotBeNull();
        left.ShouldBeSameAs(right, "precondition: one store instance serves one shared cached object");

        var reloaded = await CreateStore().GetAsync(sessionId);
        reloaded.ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Select(e => e.Content).ShouldBe(["seed", "shared-append"]);
        reloaded.Metadata["sharedKey"]?.ToString().ShouldBe("sharedValue");
        reloaded.Metadata["seedKey"]?.ToString().ShouldBe("seedValue");
    }

    [Fact]
    public async Task SameInstance_MutationsKeepCachedSessionCoherent()
    {
        // After a narrow mutation the store's own warm read must agree with a cold read - a
        // mutation that only wrote the row would leave the cache serving pre-mutation state.
        var sessionId = await ArrangeSessionAsync("s-cache-coherence", "agent-h");
        var store = CreateStore();

        _ = await store.GetAsync(sessionId); // warm the cache

        await store.AppendEntriesAsync(sessionId, [new SessionEntry { Role = MessageRole.Assistant, Content = "warm-append" }]);
        await store.PatchMetadataAsync(sessionId, new Dictionary<string, object?> { ["warmKey"] = "warmValue" });
        await store.TransitionStatusAsync(sessionId, [SessionStatus.Active], SessionStatus.Suspended);

        var warm = await store.GetAsync(sessionId);
        warm.ShouldNotBeNull();
        warm.GetHistorySnapshot().Select(e => e.Content).ShouldBe(["seed", "warm-append"]);
        warm.Metadata["warmKey"]?.ToString().ShouldBe("warmValue");
        warm.Status.ShouldBe(SessionStatus.Suspended);

        var cold = await CreateStore().GetAsync(sessionId);
        cold.ShouldNotBeNull();
        cold.GetHistorySnapshot().Select(e => e.Content).ShouldBe(["seed", "warm-append"]);
        cold.Metadata["warmKey"]?.ToString().ShouldBe("warmValue");
        cold.Status.ShouldBe(SessionStatus.Suspended);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(_connectionString);
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked handle on Windows must not fail the run.
        }
    }
}
