using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Job-lifecycle session cleanup (#2893): deleting a cron job must also reclaim the
/// <c>cron:</c>-scoped run sessions that job created, not just the pinned conversation.
/// </summary>
/// <remarks>
/// <para>
/// Before #2893 the only session-deletion path was the per-run
/// <see cref="CronJob.DeleteAfterRun"/> opt-in, so every deleted non-ephemeral job stranded one
/// session plus one transcript per historical run. These tests assert the deletion as an
/// <b>observable on the session store</b> - which ids were passed to
/// <see cref="ISessionStore.DeleteAsync"/> - never on a flag or a log line.
/// </para>
/// <para>
/// The eligibility rule under test is the same prefix convention the legacy-conversation
/// migration uses: a run session id is <c>cron:{jobIdSlug}:{timestamp}:{guid}</c>. Sessions that
/// are not <c>cron:</c>-scoped, and cron sessions belonging to a <em>different</em> job, must
/// survive. The legacy jobId-less form <c>cron:{timestamp}:{guid}</c> cannot be attributed to a
/// job and is therefore also left alone.
/// </para>
/// </remarks>
public sealed class CronJobDeleteSessionCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // ── AC1 + AC3: the job's own cron sessions are deleted ────────────────────────

    [Fact]
    public async Task DeleteJob_DeletesTheCronSessionsProducedByItsRuns()
    {
        // AC3 verbatim: create a job, run it twice with DeleteAfterRun = false, delete the job,
        // and assert zero surviving sessions for that job id.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore();
        var action = new SequentialSessionAction("test-action", sessions, "agent-a", "cron:job-1:run-a", "cron:job-1:run-b");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        job.DeleteAfterRun.ShouldBeFalse();
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [action], sessions);
        await scheduler.RunNowAsync(JobId.From("job-1"));
        await scheduler.RunNowAsync(JobId.From("job-1"));

        // Nothing was reclaimed per-run, because the job did not opt in.
        sessions.Deleted.ShouldBeEmpty();
        sessions.SurvivingIdsFor("job-1").Count.ShouldBe(2);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        sessions.SurvivingIdsFor("job-1").ShouldBeEmpty();
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    // ── AC2: nothing outside the job's own cron scope is touched ─────────────────

    [Fact]
    public async Task DeleteJob_NeverDeletesSessionsThatAreNotCronScoped()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore();
        // A long-lived per-agent session an action happened to reuse. A misattributed delete here
        // is the destructive failure mode this clause exists to prevent.
        sessions.Seed("agent-a", "agent:agent-a:main");
        sessions.Seed("agent-a", "signalr:abc123");
        sessions.Seed("agent-a", "cron:job-1:20260801:aaa");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        sessions.Deleted.Select(id => id.Value).ShouldBe(["cron:job-1:20260801:aaa"]);
        sessions.SurvivingIds().ShouldBe(["agent:agent-a:main", "signalr:abc123"], ignoreOrder: true);
    }

    [Fact]
    public async Task DeleteJob_DoesNotDeleteCronSessionsBelongingToADifferentJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore();
        sessions.Seed("agent-a", "cron:job-1:20260801:aaa");
        sessions.Seed("agent-a", "cron:job-2:20260801:bbb");
        // Prefix-collision guard: `job-1` must not match `job-10`'s sessions.
        sessions.Seed("agent-a", "cron:job-10:20260801:ccc");

        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        sessions.Deleted.Select(id => id.Value).ShouldBe(["cron:job-1:20260801:aaa"]);
        sessions.SurvivingIds().ShouldBe(["cron:job-2:20260801:bbb", "cron:job-10:20260801:ccc"], ignoreOrder: true);
    }

    [Fact]
    public async Task DeleteJob_LeavesTheLegacyJobIdLessCronSessionFormAlone()
    {
        // `cron:{timestamp}:{guid}` predates the jobId slug and cannot be attributed to any job.
        // Deleting it would be a guess, and a wrong guess destroys another job's transcript.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore();
        sessions.Seed("agent-a", "cron:20260801120000:deadbeef");

        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        sessions.Deleted.ShouldBeEmpty();
        sessions.SurvivingIds().ShouldBe(["cron:20260801120000:deadbeef"]);
    }

    // ── AC4: a session-store failure is contained ────────────────────────────────

    [Fact]
    public async Task DeleteJob_StillRemovesTheJobRow_WhenTheSessionDeleteThrows()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore { ThrowOnDelete = new InvalidOperationException("session store is down") };
        sessions.Seed("agent-a", "cron:job-1:20260801:aaa");

        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions);

        // Does not throw out of the delete - the API delete path must not surface a 500 for a
        // best-effort reclamation that failed.
        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        // Non-vacuity: the cleanup must actually have been ATTEMPTED and hit the throwing delete.
        // Without this the test passes identically on a build that performs no cleanup at all.
        sessions.DeleteAttempts.ShouldBe(["cron:job-1:20260801:aaa"]);

        // And the job row is gone regardless, so the delete is not silently a no-op the caller
        // would have to retry forever against a permanently broken session store.
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJob_StillRemovesTheJobRow_WhenTheSessionEnumerationThrows()
    {
        // The failure can land on the read as easily as on the write; both must be contained.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore { ThrowOnList = new InvalidOperationException("session store is down") };

        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        // Non-vacuity: the enumeration was attempted (and threw). A build with no cleanup at all
        // would never call ListAsync and would pass this test for the wrong reason.
        sessions.ListCalls.ShouldBe(1);
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJob_StillArchivesTheConversation_WhenTheSessionCleanupFails()
    {
        // Cleanup failure must not regress the pre-#2893 behaviour it was added alongside.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore { ThrowOnDelete = new InvalidOperationException("boom") };
        sessions.Seed("agent-a", "cron:job-1:20260801:aaa");

        var archived = new List<ConversationId>();
        var conversations = new Mock<IConversationStore>();
        conversations
            .Setup(store => store.ArchiveAsync(
                It.IsAny<ConversationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((ConversationId id, string _, string _, string _, CancellationToken _) => archived.Add(id))
            .Returns(Task.CompletedTask);

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = ConversationId.From("conv-abc")
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions, conversations.Object);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        archived.ShouldHaveSingleItem().Value.ShouldBe("conv-abc");
        sessions.DeleteAttempts.ShouldBe(["cron:job-1:20260801:aaa"]);
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    // ── AC5: idempotence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteJob_WithNoRuns_IsASessionNoOp()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        // The no-op is an OBSERVED empty sweep, not an absent one: the cleanup ran, enumerated the
        // job's sessions, and found nothing eligible. Asserting only `Deleted.ShouldBeEmpty()`
        // would hold just as well on a build that never looks.
        sessions.ListCalls.ShouldBe(1);
        sessions.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteJob_ThatDoesNotExist_TouchesTheSessionStoreNotAtAll()
    {
        // The missing-job early-out must stay ahead of the cleanup: a delete for an unknown id
        // has no owned sessions to reason about, and enumerating anyway would be a prefix guess.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore();
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions);

        await scheduler.DeleteJobAsync(JobId.From("never-existed"));

        sessions.ListCalls.ShouldBe(0);
        sessions.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteJob_IsIdempotent_ASecondDeleteDeletesNothingFurther()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessions = new FakeSessionStore();
        sessions.Seed("agent-a", "cron:job-1:20260801:aaa");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], sessions);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));
        var afterFirst = sessions.Deleted.Count;
        afterFirst.ShouldBe(1);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        sessions.Deleted.Count.ShouldBe(afterFirst);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        ISessionStore sessionStore,
        IConversationStore? conversationStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sessionStore);
        services.AddSingleton(conversationStore ?? Mock.Of<IConversationStore>());
        var provider = services.BuildServiceProvider();

        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            NullLogger<CronScheduler>.Instance,
            new FixedTimeProvider(Now));
    }

    private sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Records a different session id on each invocation and registers it with the session store,
    /// modelling a job that produces one run session per run.
    /// </summary>
    private sealed class SequentialSessionAction(
        string actionType,
        FakeSessionStore store,
        string agentId,
        params string[] sessionIds) : ICronAction
    {
        private int _index;

        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            var id = sessionIds[Math.Min(_index++, sessionIds.Length - 1)];
            context.RecordSessionId(SessionId.From(id));
            // Model the row the run's session store write would have left behind.
            store.Seed(agentId, id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly List<GatewaySession> _sessions = [];

        public List<SessionId> Deleted { get; } = [];

        /// <summary>
        /// Every id the cleanup asked to delete, recorded <b>before</b> <see cref="ThrowOnDelete"/>
        /// is honoured. This is what makes the sad-path tests non-vacuous: it distinguishes "the
        /// cleanup ran and the store refused" from "no cleanup ran at all".
        /// </summary>
        public List<string> DeleteAttempts { get; } = [];

        public int ListCalls { get; private set; }

        public Exception? ThrowOnDelete { get; init; }

        public Exception? ThrowOnList { get; init; }

        public void Seed(string agentId, string sessionId)
        {
            if (_sessions.Any(s => string.Equals(s.SessionId.Value, sessionId, StringComparison.Ordinal)))
                return;

            _sessions.Add(new GatewaySession
            {
                SessionId = SessionId.From(sessionId),
                AgentId = AgentId.From(agentId)
            });
        }

        public IReadOnlyList<string> SurvivingIds()
            => _sessions.Select(s => s.SessionId.Value).ToList();

        public IReadOnlyList<string> SurvivingIdsFor(string jobId)
            => _sessions
                .Select(s => s.SessionId.Value)
                .Where(id => id.StartsWith($"cron:{jobId}:", StringComparison.Ordinal))
                .ToList();

        public Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            DeleteAttempts.Add(sessionId.Value);

            if (ThrowOnDelete is not null)
                throw ThrowOnDelete;

            Deleted.Add(sessionId);
            _sessions.RemoveAll(s => s.SessionId == sessionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            if (ThrowOnList is not null)
                throw ThrowOnList;

            IReadOnlyList<GatewaySession> result = agentId is { } id
                ? _sessions.Where(s => s.AgentId == id).ToList()
                : _sessions.ToList();
            return Task.FromResult(result);
        }

        public Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_sessions.FirstOrDefault(s => s.SessionId == sessionId));

        public Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(AgentId agentId, ChannelKey channelType, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);

        public Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(ConversationId conversationId, AgentId? agentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);

        public Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(AgentId agentId, ExistenceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
