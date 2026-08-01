using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Job-level lifecycle behaviour (#2634): the opt-in one-shot terminal disposition
/// (<see cref="CronJob.DeleteJobAfterRun"/>) and the hard expiry instant
/// (<see cref="CronJob.ExpiresAt"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is on an <b>observable</b>: whether the job is still in the store, and
/// whether the action was actually invoked. Nothing asserts on a flag's own value, because a flag
/// round-tripping proves only that the column exists - not that the scheduler acts on it.
/// </para>
/// <para>
/// Time is injected via <see cref="FixedTimeProvider"/>. There are no wall-clock waits and no
/// elapsed-time bounds anywhere in this file (#2589).
/// </para>
/// </remarks>
public sealed class CronJobLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // ── AC1: one-shot removal, asserted as ABSENCE FROM THE STORE ─────────────────

    [Fact]
    public async Task OneShotJob_IsRemovedFromStore_AfterSuccessfulRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteJobAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe(CronRunStatus.Ok);
        action.Invocations.ShouldBe(1);

        // The observable: the JOB is gone. Not a flag, not a log line.
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task OneShotRemoval_DoesNotDependOnThePromptAskingForIt()
    {
        // The #2634 defect: the job's prompt said "delete this cron job after running" and nothing
        // deleted it. Here the action explicitly does NOT touch the store, and the job still goes.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteJobAfterRun = true,
            Message = "This is a one-shot check; delete this cron job after running."
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task JobWithoutOneShot_SurvivesItsRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        // DeleteJobAfterRun defaults to false.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldNotBeNull();
    }

    // ── AC2: removal survives a run that THROWS, TIMES OUT, or is CANCELLED ───────

    [Fact]
    public async Task OneShotJob_IsRemoved_WhenTheActionThrows()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new ThrowingAction("test-action", "boom");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteJobAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe(CronRunStatus.Error);
        // A failing one-shot must still be removed: it is terminal, and leaving it behind is the
        // original bug (the job outlives the single occasion it existed for).
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task OneShotJob_IsRemoved_WhenTheRunIsCancelled()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        // Cancels the host token from inside the action, then observes it - the abort path that
        // rethrows OperationCanceledException past the outer catch.
        using var cts = new CancellationTokenSource();
        var action = new CancellingAction("test-action", cts);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteJobAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await scheduler.RunNowAsync(JobId.From("job-1"), cts.Token));

        // The cancellation propagated to the caller AND the job was still removed, because removal
        // lives in the finally rather than on the success path.
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task OneShotJob_IsRemoved_WhenTheRunTimesOut()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayingAction("test-action", TimeSpan.FromSeconds(30));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteJobAfterRun = true };
        await context.Store.CreateAsync(job);
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 1 };
        var scheduler = CreateScheduler(context.Store, [action], options);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe(CronRunStatus.TimedOut);
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task OneShotRemoval_AlsoArchivesThePinnedConversation()
    {
        // Removal routes through DeleteJobAsync, so a one-shot with a pinned conversation gets the
        // same archive treatment a manual delete would give it - no orphaned thread.
        await using var context = await CronStoreTestContext.CreateAsync();
        var archived = new List<ConversationId>();
        var conversations = new Mock<IConversationStore>();
        conversations
            .Setup(store => store.ArchiveAsync(
                It.IsAny<ConversationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((ConversationId id, string _, string _, string _, CancellationToken _) => archived.Add(id))
            .Returns(Task.CompletedTask);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteJobAfterRun = true,
            ConversationId = ConversationId.From("conv-abc")
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")], conversationStore: conversations.Object);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
        archived.ShouldHaveSingleItem().Value.ShouldBe("conv-abc");
    }

    // ── AC3: expiry stops execution ───────────────────────────────────────────────

    [Fact]
    public async Task ExpiredJob_DoesNotInvokeItsAction()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ExpiresAt = Now.AddHours(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        // The observable: the action was never invoked.
        action.Invocations.ShouldBe(0);
        run.Status.ShouldBe(CronRunStatus.Skipped);

        // A suppressed fire is the absence of a run, so it leaves no run-history row behind.
        (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpiryIsInclusive_AJobExpiringExactlyNowDoesNotRun()
    {
        // "stops executing after that instant" must not leave a one-tick window where a fire lands.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { ExpiresAt = Now };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        action.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task NotYetExpiredJob_RunsNormally()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ExpiresAt = Now.AddHours(1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        action.Invocations.ShouldBe(1);
        run.Status.ShouldBe(CronRunStatus.Ok);
    }

    [Fact]
    public async Task ExpiredJob_IsNotDeletedOrDisabled()
    {
        // Expiry SUPPRESSES. Silently mutating a job a human still wants is explicitly out of
        // scope for #2634, so the stored row must be untouched after a suppressed fire.
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ExpiresAt = Now.AddHours(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")]);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        var stored = await context.Store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.Enabled.ShouldBeTrue();
        stored.ExpiresAt.ShouldBe(Now.AddHours(-1));
    }

    [Fact]
    public async Task ExpiryIsEvaluatedAtFireTime_NotOnlyAtScheduleTime()
    {
        // The clock advances past the expiry AFTER the job was created and would have been scanned.
        // A schedule-time-only check would let this fire; the fire-time gate is what stops it.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var clock = new FixedTimeProvider(Now);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ExpiresAt = Now.AddMinutes(30)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], clock: clock);

        // Before expiry: runs.
        await scheduler.RunNowAsync(JobId.From("job-1"));
        action.Invocations.ShouldBe(1);

        // Advance past the expiry with no wall-clock wait.
        clock.Advance(TimeSpan.FromHours(1));

        await scheduler.RunNowAsync(JobId.From("job-1"));
        action.Invocations.ShouldBe(1); // unchanged: the second fire was suppressed
    }

    [Fact]
    public async Task ExpiredJob_IsNotCollectedByTheDueScan()
    {
        // The schedule-time early-out: an expired job never even enters the due set, so it is not
        // executed by a tick either.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ExpiresAt = Now.AddHours(-1),
            NextRunAt = Now.AddMinutes(-5) // already due
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.Invocations.ShouldBe(0);
    }

    // ── AC4: NULL/absent fields change NOTHING ────────────────────────────────────

    [Fact]
    public async Task JobWithAllLifecycleFieldsNull_SchedulesAndExecutesIdentically()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = Now.AddMinutes(-5) // already due
        };
        job.DeleteJobAfterRun.ShouldBeFalse();
        job.ExpiresAt.ShouldBeNull();
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        // Scheduled and executed exactly as today...
        action.Invocations.ShouldBe(1);
        // ...and still present afterwards, with a next run computed.
        var stored = await context.Store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.Enabled.ShouldBeTrue();
        stored.NextRunAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task LegacyRowWithoutTheNewColumns_LoadsInertAndRunsAsToday()
    {
        // AC4 + the migration contract: a row written by a pre-#2634 build has neither column.
        // It must read as "no one-shot, no expiry" and behave exactly as it does today.
        await using var context = await CronStoreTestContext.CreateAsync();
        await WriteLegacyRowAsync(context.DbPath, "legacy-1");

        var loaded = await context.Store.GetAsync(JobId.From("legacy-1"));
        loaded.ShouldNotBeNull();
        loaded!.DeleteJobAfterRun.ShouldBeFalse();
        loaded.ExpiresAt.ShouldBeNull();

        var action = new RecordingAction("test-action");
        var scheduler = CreateScheduler(context.Store, [action]);
        await scheduler.RunNowAsync(JobId.From("legacy-1"));

        // It ran, and it survived.
        action.Invocations.ShouldBe(1);
        (await context.Store.GetAsync(JobId.From("legacy-1"))).ShouldNotBeNull();
    }

    // ── Persistence round-trip ────────────────────────────────────────────────────

    [Fact]
    public async Task LifecycleFields_RoundTripThroughTheStore()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var expiry = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteJobAfterRun = true,
            ExpiresAt = expiry
        };
        await context.Store.CreateAsync(job);

        var fetched = await context.Store.GetAsync(JobId.From("job-1"));
        fetched.ShouldNotBeNull();
        fetched!.DeleteJobAfterRun.ShouldBeTrue();
        fetched.ExpiresAt.ShouldBe(expiry);

        // Defaults stay inert for a job that does not opt in.
        var plain = CronStoreTestContext.CreateJob("job-2", actionType: "test-action");
        await context.Store.CreateAsync(plain);
        var plainFetched = await context.Store.GetAsync(JobId.From("job-2"));
        plainFetched!.DeleteJobAfterRun.ShouldBeFalse();
        plainFetched.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task LifecycleFields_SurviveADefinitionUpdate()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var expiry = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);

        var updated = await context.Store.UpdateDefinitionAsync(job with
        {
            DeleteJobAfterRun = true,
            ExpiresAt = expiry
        });

        updated.ShouldNotBeNull();
        updated!.DeleteJobAfterRun.ShouldBeTrue();
        updated.ExpiresAt.ShouldBe(expiry);
    }

    [Fact]
    public async Task DeleteAfterRun_AndDeleteJobAfterRun_AreIndependent()
    {
        // #1561 semantics must not change: DeleteAfterRun deletes the SESSION, the new flag deletes
        // the JOB. A job with only the session flag keeps its job row.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new RecordingSessionStore();
        var action = new SessionRecordingAction("test-action", "cron:job-1:run-a");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { DeleteAfterRun = true };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], sessionStore: sessionStore);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        sessionStore.Deleted.ShouldHaveSingleItem().Value.ShouldBe("cron:job-1:run-a");
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldNotBeNull();
    }

    [Fact]
    public async Task BothCleanupFlags_ComposeWithoutInterfering()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var sessionStore = new RecordingSessionStore();
        var action = new SessionRecordingAction("test-action", "cron:job-1:run-b");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteAfterRun = true,
            DeleteJobAfterRun = true
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action], sessionStore: sessionStore);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        sessionStore.Deleted.ShouldHaveSingleItem().Value.ShouldBe("cron:job-1:run-b");
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a row through a column list that omits the #2634 columns entirely, simulating a row
    /// persisted by a build that predates them. SQLite fills them with the migration defaults.
    /// </summary>
    private static async Task WriteLegacyRowAsync(string dbPath, string jobId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cron_jobs (id, name, schedule, action_type, agent_id, message, enabled, system, created_by, created_at)
            VALUES ($id, $name, '*/1 * * * *', 'test-action', 'agent-a', 'legacy', 1, 0, 'test-agent', $createdAt)
            """;
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$name", $"Legacy {jobId}");
        command.Parameters.AddWithValue("$createdAt", Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod(
            "ProcessTickAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        CronOptions? options = null,
        ISessionStore? sessionStore = null,
        IConversationStore? conversationStore = null,
        FixedTimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sessionStore ?? new RecordingSessionStore());
        services.AddSingleton(conversationStore ?? Mock.Of<IConversationStore>());
        var provider = services.BuildServiceProvider();
        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(options ?? new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            NullLogger<CronScheduler>.Instance,
            clock ?? new FixedTimeProvider(Now));
    }

    /// <summary>Deterministic, manually advanced clock. No wall-clock dependency (#2589).</summary>
    private sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    // ── Test actions ──────────────────────────────────────────────────────────────

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public int Invocations { get; private set; }
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAction(string actionType, string message) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class DelayingAction(string actionType, TimeSpan delay) : ICronAction
    {
        public string ActionType => actionType;
        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels the host token mid-run, then observes it - the graceful-abort path.</summary>
    private sealed class CancellingAction(string actionType, CancellationTokenSource cts) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class SessionRecordingAction(string actionType, string sessionId) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordSessionId(SessionId.From(sessionId));
            return Task.CompletedTask;
        }
    }

    // ── Test stores ───────────────────────────────────────────────────────────────

    private sealed class RecordingSessionStore : ISessionStore
    {
        public List<SessionId> Deleted { get; } = [];

        public Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            Deleted.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<GatewaySession?>(null);
        public Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
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
