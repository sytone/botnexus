using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Issue #2903: <c>ArchiveAsync</c> used to seal a session while a run could still be writing to
/// it, and the SQLite implementation's destructive <c>ReplaceHistoryAsync</c> turned that race into
/// silent turn loss. These tests pin the fence: the drain runs before the seal, it is scoped to the
/// exact session, a drain that times out fails cleanly with nothing written, and <c>DeleteAsync</c>
/// is left alone.
/// </summary>
public sealed class SessionArchiveRunDrainTests
{
    private static readonly SessionId Target = SessionId.From("archive-target");
    private static readonly AgentId Agent = AgentId.From("agent-archive");

    public static IEnumerable<object[]> StoreHarnesses()
    {
        yield return ["in-memory", () => (IStoreHarness)new InMemoryHarness()];
        yield return ["file", () => (IStoreHarness)new FileHarness()];
        yield return ["sqlite", () => (IStoreHarness)new SqliteHarness()];
    }

    // ---- AC1: the drain runs, and it runs BEFORE the seal is committed -------------------

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ArchiveAsync_DrainsActiveRun_BeforeCommittingTheSeal(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedAsync(harness.Store, Target);

        GatewaySessionStatus? statusObservedAtDrainTime = null;
        var drain = new RecordingDrain(SessionDrainOutcome.Drained, async sessionId =>
        {
            var session = await harness.Store.GetAsync(sessionId);
            statusObservedAtDrainTime = session?.Status;
        });
        harness.Store.ConfigureArchiveDrain(drain);

        await harness.Store.ArchiveAsync(Target);

        drain.Calls.ShouldBe([Target]);
        // The store must not have sealed (or removed) anything by the time the drain was consulted.
        statusObservedAtDrainTime.ShouldBe(GatewaySessionStatus.Active);
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ArchiveAsync_WithNoActiveRun_ProceedsNormally(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedAsync(harness.Store, Target);

        var drain = new RecordingDrain(SessionDrainOutcome.NoActiveRun);
        harness.Store.ConfigureArchiveDrain(drain);

        await harness.Store.ArchiveAsync(Target);

        drain.Calls.ShouldBe([Target]);
        var session = await harness.Store.GetAsync(Target);
        // "Archived" means different things per store; what must hold everywhere is that the
        // session is no longer Active.
        (session?.Status ?? GatewaySessionStatus.Sealed).ShouldNotBe(GatewaySessionStatus.Active);
    }

    // ---- AC2: a drain that will not complete fails cleanly, and changes nothing ----------

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ArchiveAsync_WhenDrainTimesOut_ThrowsDistinguishableError(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedAsync(harness.Store, Target);

        harness.Store.ConfigureArchiveDrain(
            new RecordingDrain(SessionDrainOutcome.TimedOut),
            TimeSpan.FromMilliseconds(50));

        var ex = await Should.ThrowAsync<SessionArchiveDrainTimeoutException>(
            () => harness.Store.ArchiveAsync(Target));

        ex.SessionId.ShouldBe(Target);
        ex.Timeout.ShouldBe(TimeSpan.FromMilliseconds(50));
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ArchiveAsync_WhenDrainTimesOut_LeavesSessionAndHistoryIntact(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedAsync(harness.Store, Target, entryCount: 3);

        harness.Store.ConfigureArchiveDrain(
            new RecordingDrain(SessionDrainOutcome.TimedOut),
            TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<SessionArchiveDrainTimeoutException>(
            () => harness.Store.ArchiveAsync(Target));

        var session = await harness.Store.GetAsync(Target);
        session.ShouldNotBeNull();
        session.Status.ShouldBe(GatewaySessionStatus.Active);
        session.GetHistorySnapshot().Count.ShouldBe(3);
    }

    // ---- AC3: the fence is scoped to the exact session -----------------------------------

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ArchiveAsync_FencesOnlyTheTargetSession(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        var other = SessionId.From("unrelated-session");
        await SeedAsync(harness.Store, Target);
        await SeedAsync(harness.Store, other);

        var drain = new RecordingDrain(SessionDrainOutcome.Drained);
        harness.Store.ConfigureArchiveDrain(drain);

        await harness.Store.ArchiveAsync(Target);

        drain.Calls.ShouldBe([Target]);
        drain.Calls.ShouldNotContain(other);

        var untouched = await harness.Store.GetAsync(other);
        untouched.ShouldNotBeNull();
        untouched.Status.ShouldBe(GatewaySessionStatus.Active);
    }

    // ---- AC4: DeleteAsync semantics are unchanged ----------------------------------------

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task DeleteAsync_DoesNotConsultTheDrain_AndStillDeletes(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedAsync(harness.Store, Target);

        // A drain that would refuse an archive must have no effect at all on delete.
        var drain = new RecordingDrain(SessionDrainOutcome.TimedOut);
        harness.Store.ConfigureArchiveDrain(drain, TimeSpan.FromMilliseconds(50));

        await harness.Store.DeleteAsync(Target);

        drain.Calls.ShouldBeEmpty();
        (await harness.Store.GetAsync(Target)).ShouldBeNull();
    }

    // ---- AC5: the concurrency test -------------------------------------------------------

    [Fact]
    public async Task ArchiveAsync_ConcurrentWithAppendingRun_NeverLosesTurnsAndNeverSealsOverThem()
    {
        using var harness = new SqliteHarness();
        var store = harness.Store;
        await SeedAsync(store, Target, entryCount: 1);

        const int TurnsToAppend = 20;

        // The "run": appends turns one at a time, exactly as a live agent turn would.
        var runGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appended = 0;
        var runFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = Task.Run(async () =>
        {
            try
            {
                runGate.SetResult();
                for (var i = 0; i < TurnsToAppend; i++)
                {
                    var result = await store.AppendEntriesAsync(
                        Target,
                        [new SessionEntry { Role = MessageRole.Assistant, Content = $"turn-{i}" }]);

                    // Once the session is sealed the append is refused with Conflict; that is the
                    // contract, and it means the run stops rather than resurrecting a sealed row.
                    if (result.Outcome != SessionMutationOutcome.Applied)
                        break;

                    Interlocked.Increment(ref appended);
                    await Task.Yield();
                }
            }
            finally
            {
                runFinished.SetResult();
            }
        });

        // The drain models the real fence: it waits for the run to settle before returning.
        store.ConfigureArchiveDrain(
            new RecordingDrain(SessionDrainOutcome.Drained, async _ => await runFinished.Task),
            TimeSpan.FromSeconds(10));

        await runGate.Task;
        await store.ArchiveAsync(Target);
        await run;

        var appendedCount = Volatile.Read(ref appended);
        appendedCount.ShouldBe(TurnsToAppend, "the drain must let the run finish before the seal");

        var sealedSession = await store.GetAsync(Target);
        sealedSession.ShouldNotBeNull();
        sealedSession.Status.ShouldBe(GatewaySessionStatus.Sealed);

        // No silent turn loss: every appended turn survived the archive's history rewrite.
        var contents = sealedSession.GetHistorySnapshot().Select(e => e.Content).ToList();
        for (var i = 0; i < TurnsToAppend; i++)
            contents.ShouldContain($"turn-{i}");

        // And a sealed session gains nothing afterwards.
        var postSeal = await store.AppendEntriesAsync(
            Target,
            [new SessionEntry { Role = MessageRole.Assistant, Content = "post-seal" }]);
        postSeal.Outcome.ShouldBe(SessionMutationOutcome.Conflict);

        var reread = await store.GetAsync(Target);
        reread.ShouldNotBeNull();
        reread.GetHistorySnapshot().Select(e => e.Content).ShouldNotContain("post-seal");
    }

    // ---- back-compat: no drain configured -----------------------------------------------

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ArchiveAsync_WithNoDrainConfigured_StillArchives(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedAsync(harness.Store, Target);

        await harness.Store.ArchiveAsync(Target);

        var session = await harness.Store.GetAsync(Target);
        (session?.Status ?? GatewaySessionStatus.Sealed).ShouldNotBe(GatewaySessionStatus.Active);
    }

    private static async Task SeedAsync(SessionStoreBase store, SessionId sessionId, int entryCount = 0)
    {
        var session = await store.GetOrCreateAsync(sessionId, Agent);
        if (entryCount > 0)
        {
            session.AddEntries(
                [.. Enumerable.Range(0, entryCount)
                    .Select(i => new SessionEntry { Role = MessageRole.User, Content = $"seed-{i}" })]);
        }

        session.Status = GatewaySessionStatus.Active;
        await store.SaveAsync(session);
    }

    /// <summary>
    /// Test double for <see cref="ISessionRunDrain"/> that records exactly which sessions were
    /// fenced (so AC3 scoping is observable) and can run an assertion hook at drain time (so AC1
    /// ordering is observable).
    /// </summary>
    private sealed class RecordingDrain(SessionDrainOutcome outcome, Func<SessionId, Task>? onDrain = null)
        : ISessionRunDrain
    {
        private readonly List<SessionId> _calls = [];
        private readonly Lock _sync = new();

        public IReadOnlyList<SessionId> Calls
        {
            get { lock (_sync) return [.. _calls]; }
        }

        public async Task<SessionDrainOutcome> DrainAsync(
            SessionId sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            lock (_sync) _calls.Add(sessionId);
            if (onDrain is not null)
                await onDrain(sessionId);
            return outcome;
        }
    }

    public interface IStoreHarness : IDisposable
    {
        SessionStoreBase Store { get; }
    }

    private sealed class InMemoryHarness : IStoreHarness
    {
        public SessionStoreBase Store { get; } = new InMemorySessionStore(null, new InMemoryConversationStore());
        public void Dispose() { }
    }

    private sealed class FileHarness : IStoreHarness
    {
        private readonly MockFileSystem _fileSystem = new();
        private readonly string _storePath = Path.Combine(Path.GetTempPath(), "SessionArchiveRunDrainTests", Guid.NewGuid().ToString("N"));

        public FileHarness()
        {
            _fileSystem.Directory.CreateDirectory(_storePath);
            Store = new FileSessionStore(_storePath, NullLogger<FileSessionStore>.Instance, _fileSystem, new InMemoryConversationStore());
        }

        public SessionStoreBase Store { get; }

        public void Dispose()
        {
            if (_fileSystem.Directory.Exists(_storePath))
                _fileSystem.Directory.Delete(_storePath, true);
        }
    }

    private sealed class SqliteHarness : IStoreHarness
    {
        private readonly string _directoryPath;
        private readonly InMemoryConversationStore _conversations = new();

        public SqliteHarness()
        {
            _directoryPath = Path.Combine(AppContext.BaseDirectory, "SessionArchiveRunDrainTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directoryPath);
            var dbPath = Path.Combine(_directoryPath, "sessions.db");
            Store = new SqliteSessionStore($"Data Source={dbPath};Pooling=False", NullLogger<SqliteSessionStore>.Instance, _conversations);
        }

        public SqliteSessionStore Store { get; }

        SessionStoreBase IStoreHarness.Store => Store;

        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
