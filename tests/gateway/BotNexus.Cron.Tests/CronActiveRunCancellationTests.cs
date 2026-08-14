using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
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
/// #3160: deleting or disabling a cron job must abort the run that job has in flight.
/// </summary>
/// <remarks>
/// <para>
/// Before #3160 the scheduler kept no <c>jobId -&gt; CancellationTokenSource</c> registry at all -
/// <c>_jobLocks</c> is a serialisation mutex and the per-run timeout CTS was a method local. So
/// <see cref="CronScheduler.DeleteJobAsync"/> removed the row and returned while the action kept
/// running: still burning a model turn, still writing into a conversation that had just been
/// archived, racing the session cleanup that was concurrently deleting the very rows it wrote, and
/// still able to fire a failure alert for a job the operator had already removed.
/// </para>
/// <para>
/// Every assertion here is on an <b>observable</b> - the token the action was actually handed, the
/// status persisted to run history, what the alert sink received, the order the session store was
/// called in - never on a flag or a log line.
/// </para>
/// </remarks>
public sealed class CronActiveRunCancellationTests
{
    // ── AC1 + AC2: delete cancels the in-flight run and records an operator abort ─────────

    [Fact]
    public async Task DeleteJob_SignalsTheActiveRunsCancellationToken_BeforeItReturns()
    {
        // AC1 verbatim. The token asserted here is the one the ACTION was handed, not a token the
        // scheduler kept to itself - a registry that cancels something the action never sees would
        // satisfy a weaker assertion while changing nothing for the run that is actually burning.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("test-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));

        var scheduler = CreateScheduler(context.Store, [action]);
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        action.ObservedToken.IsCancellationRequested.ShouldBeFalse("precondition: the run is healthy and in flight");

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        // The whole point of the fix: signalled by the time DeleteJobAsync hands control back.
        action.ObservedToken.IsCancellationRequested.ShouldBeTrue(
            "deleting a job must cancel the token its in-flight action is executing under");

        await runTask;
    }

    [Fact]
    public async Task DeleteJob_RecordsTheRunAsOperatorAborted_DistinctFromTimeoutAndError()
    {
        // AC2. A distinct status is the point: an operator scanning history must be able to tell
        // "I killed this" from "it broke" and from "it ran too long". Collapsing the three would
        // send someone debugging a job that was deliberately removed.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("test-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));

        var scheduler = CreateScheduler(context.Store, [action]);
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await scheduler.DeleteJobAsync(JobId.From("job-1"));
        var run = await runTask;

        run.Status.ShouldBe(CronRunStatus.Aborted);
        run.Status.ShouldNotBe(CronRunStatus.TimedOut);
        run.Status.ShouldNotBe(CronRunStatus.Error);
        run.Error.ShouldBe(CronScheduler.OperatorAbortReason);
    }

    [Fact]
    public async Task AbortedStatus_IsDistinctFromEveryOtherTerminalStatus()
    {
        // Guards the contract itself: `aborted` must not collide with an existing persisted value,
        // because history rows and the digest parser compare against these exact strings.
        CronRunStatus.Aborted.ShouldBe("aborted");
        var all = new[]
        {
            CronRunStatus.Ok, CronRunStatus.Error, CronRunStatus.TimedOut, CronRunStatus.Running,
            CronRunStatus.NoToolCalls, CronRunStatus.DeliveryFailed, CronRunStatus.Skipped,
            CronRunStatus.Missed, CronRunStatus.Aborted
        };
        all.Distinct(StringComparer.Ordinal).Count().ShouldBe(all.Length);
    }

    [Fact]
    public async Task HostCancellation_IsStillRecordedAsError_NotAsAnOperatorAbort()
    {
        // Behaviour parity guard. A gateway shutdown is NOT an operator abort, and the pre-#3160
        // shape for it (error, plus a rethrown OperationCanceledException) must survive untouched -
        // otherwise every shutdown would start masquerading as a deliberate removal.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("test-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));

        var scheduler = CreateScheduler(context.Store, [action]);
        using var cts = new CancellationTokenSource();
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"), cts.Token);
        await action.Started.Task;

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await runTask);

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Error);
        entry.Status.ShouldNotBe(CronRunStatus.Aborted);
    }

    // ── AC3: disabling an active job cancels its run ─────────────────────────────────────

    [Fact]
    public async Task CancelActiveRun_CancelsTheRunAndRecordsTheOperatorAbort()
    {
        // The shared seam both the disable paths (tool + controller) route through.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("test-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));

        var scheduler = CreateScheduler(context.Store, [action]);
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        var cancelled = await scheduler.CancelActiveRunAsync(JobId.From("job-1"));

        cancelled.ShouldBe(1, "exactly one run was in flight for that job");
        action.ObservedToken.IsCancellationRequested.ShouldBeTrue();
        (await runTask).Status.ShouldBe(CronRunStatus.Aborted);
    }

    [Fact]
    public async Task CancelActiveRun_ForAJobWithNoRunInFlight_IsANoOp()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new BlockingAction("test-action")]);

        (await scheduler.CancelActiveRunAsync(JobId.From("job-1"))).ShouldBe(0);
    }

    [Fact]
    public async Task CancelActiveRun_NeverTouchesADifferentJobsRun()
    {
        // A cancel keyed loosely (or by prefix) would let removing `job-1` kill `job-10`'s run.
        await using var context = await CronStoreTestContext.CreateAsync();
        var victim = new BlockingAction("victim-action");
        var bystander = new BlockingAction("bystander-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "victim-action"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-10", actionType: "bystander-action"));

        var scheduler = CreateScheduler(context.Store, [victim, bystander]);
        var victimTask = scheduler.RunNowAsync(JobId.From("job-1"));
        var bystanderTask = scheduler.RunNowAsync(JobId.From("job-10"));
        await victim.Started.Task;
        await bystander.Started.Task;

        await scheduler.CancelActiveRunAsync(JobId.From("job-1"));

        victim.ObservedToken.IsCancellationRequested.ShouldBeTrue();
        bystander.ObservedToken.IsCancellationRequested.ShouldBeFalse("job-10's run is unrelated");

        await victimTask;
        bystander.Release();
        await bystanderTask;
    }

    [Fact]
    public async Task DisablingAJobThroughTheCronTool_CancelsItsActiveRun()
    {
        // AC3 through the surface an operator actually uses. Asserting only on the scheduler seam
        // would leave the tool free to persist `enabled: false` and walk away, which is the bug.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("agent-prompt");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var scheduler = CreateScheduler(context.Store, [action]);
        var tool = new CronTool(context.Store, scheduler, AgentId.From("agent-a"));

        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["enabled"] = false
        });

        action.ObservedToken.IsCancellationRequested.ShouldBeTrue(
            "disabling a job must abort the run it has in flight, not just stop future fires");
        (await runTask).Status.ShouldBe(CronRunStatus.Aborted);
        (await context.Store.GetAsync(JobId.From("job-1")))!.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task AnUnrelatedToolUpdate_DoesNotCancelTheActiveRun()
    {
        // Containment: only the enabled -> disabled TRANSITION cancels. Renaming a job, or a
        // no-op update that re-asserts `enabled: true`, must leave the run alone - otherwise every
        // routine edit becomes a silent kill switch.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("agent-prompt");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var scheduler = CreateScheduler(context.Store, [action]);
        var tool = new CronTool(context.Store, scheduler, AgentId.From("agent-a"));

        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["name"] = "Renamed"
        });

        action.ObservedToken.IsCancellationRequested.ShouldBeFalse();

        action.Release();
        (await runTask).Status.ShouldBe(CronRunStatus.Ok);
    }

    // ── AC4: an operator abort delivers no failure alert ─────────────────────────────────

    [Fact]
    public async Task OperatorAbortedRun_DeliversNoFailureAlert()
    {
        // AC4. Alerts are explicitly ENABLED here, so the silence is the fix and not the default.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        var action = new BlockingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            FailureAlertsEnabled = true,
            FailureAlertConversationId = ConversationId.From("conv-alerts")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [action], alertSink: sink);
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await scheduler.CancelActiveRunAsync(JobId.From("job-1"));
        (await runTask).Status.ShouldBe(CronRunStatus.Aborted);

        sink.Alerts.ShouldBeEmpty("an operator who killed the job does not need to be alarmed about it");
    }

    [Fact]
    public async Task AnOperatorAbort_DoesNotCountTowardTheFailureAlertStreak()
    {
        // The corollary of AC4. If `aborted` joined the error streak, the NEXT genuine failure
        // would be reported at the wrong backoff position - the operator's own delete would be
        // silently blamed on the job.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        var blocking = new BlockingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            FailureAlertsEnabled = true,
            FailureAlertConversationId = ConversationId.From("conv-alerts")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [blocking], alertSink: sink);
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await blocking.Started.Task;
        await scheduler.CancelActiveRunAsync(JobId.From("job-1"));
        await runTask;

        // A run that failed for real immediately afterwards is streak position 1, so it alerts.
        var second = CreateScheduler(context.Store, [new ThrowingAction("test-action", "boom")], alertSink: sink);
        await second.RunNowAsync(JobId.From("job-1"));

        var alert = sink.Alerts.ShouldHaveSingleItem();
        alert.ConsecutiveErrorCount.ShouldBe(1, "the preceding abort is not a failure and must not extend the streak");
    }

    // ── AC5: the registry is emptied and disposed on EVERY terminal path ─────────────────

    [Fact]
    public async Task Registry_IsEmpty_AfterASuccessfulRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")]);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        scheduler.ActiveRunCount.ShouldBe(0);
    }

    [Fact]
    public async Task Registry_IsEmpty_AfterAFailedRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("test-action", "boom")]);

        (await scheduler.RunNowAsync(JobId.From("job-1"))).Status.ShouldBe(CronRunStatus.Error);

        scheduler.ActiveRunCount.ShouldBe(0);
    }

    [Fact]
    public async Task Registry_IsEmpty_AfterATimedOutRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(
            context.Store,
            [new BlockingAction("test-action")],
            options: new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 1 });

        (await scheduler.RunNowAsync(JobId.From("job-1"))).Status.ShouldBe(CronRunStatus.TimedOut);

        scheduler.ActiveRunCount.ShouldBe(0);
    }

    [Fact]
    public async Task Registry_IsEmpty_AfterAnOperatorAbortedRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("test-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [action]);

        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;
        await scheduler.DeleteJobAsync(JobId.From("job-1"));
        await runTask;

        scheduler.ActiveRunCount.ShouldBe(0);
    }

    [Fact]
    public async Task Registry_IsEmpty_AfterAHostCancelledRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("test-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [action]);

        using var cts = new CancellationTokenSource();
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"), cts.Token);
        await action.Started.Task;
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await runTask);

        scheduler.ActiveRunCount.ShouldBe(0);
    }

    [Fact]
    public async Task Registry_IsEmpty_AfterASuppressedExpiredFire()
    {
        // The #2634 expiry gate returns before a run row is ever stamped. It must not leave a
        // phantom registry entry that a later delete would try to cancel.
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")]);

        (await scheduler.RunNowAsync(JobId.From("job-1"))).Status.ShouldBe(CronRunStatus.Skipped);

        scheduler.ActiveRunCount.ShouldBe(0);
    }

    [Fact]
    public async Task Registry_DisposesTheCancellationTokenSource_OnTheTerminalPath()
    {
        // Not just removed - DISPOSED. A registry that removed the entry but leaked every CTS
        // would satisfy an "is it empty" assertion while accumulating one undisposed linked
        // source (and its registration on the host token) per run, forever.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction("test-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [action]);

        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;
        var token = action.ObservedToken;
        action.Release();
        await runTask;

        // Not just removed - DISPOSED. A registry that removed the entry but leaked every source
        // would satisfy an "is it empty" assertion while accumulating one undisposed linked source
        // (and its registration on the host token) per run, forever.
        //
        // `WaitHandle` is the observable used deliberately: it is documented to throw
        // ObjectDisposedException once the owning source is disposed. `Register` is NOT - modern
        // .NET quietly returns a no-op registration on a disposed source, so asserting on it would
        // pin framework trivia rather than this fix.
        Should.Throw<ObjectDisposedException>(() => _ = token.WaitHandle);
    }

    // ── AC6: session cleanup runs only AFTER the run observed cancellation ───────────────

    [Fact]
    public async Task DeleteJob_DeletesOwnedRunSessions_OnlyAfterTheRunObservedCancellation()
    {
        // AC6. Pre-#3160 the cleanup swept while the action was still writing, so it deleted rows
        // the live run promptly recreated (or destroyed rows mid-write). The ordering is asserted
        // on a monotonic sequence recorded by the participants themselves.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sequence = new OrderLog();
        var sessions = new OrderedSessionStore(sequence);
        sessions.Seed("agent-a", "cron:job-1:20260801:aaa");
        var action = new BlockingAction("test-action", sequence);
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));

        var scheduler = CreateScheduler(context.Store, [action], sessionStore: sessions);
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await scheduler.DeleteJobAsync(JobId.From("job-1"));
        await runTask;

        action.CancellationObservedAt.ShouldNotBeNull("the run must actually have observed the cancel");
        sessions.FirstDeleteAt.ShouldNotBeNull("non-vacuity: the cleanup must have run at all");
        sessions.FirstDeleteAt!.Value.ShouldBeGreaterThan(
            action.CancellationObservedAt!.Value,
            "session cleanup must not race a run that is still writing to those sessions");
    }

    [Fact]
    public async Task DeleteJob_ArchivesTheConversation_OnlyAfterTheRunObservedCancellation()
    {
        // Same ordering guarantee for the archive: a run still writing into a conversation that
        // was already archived resurrects it, which is exactly the state #3160 reports.
        await using var context = await CronStoreTestContext.CreateAsync();
        var sequence = new OrderLog();
        var action = new BlockingAction("test-action", sequence);
        long? archivedAt = null;
        var conversations = new Mock<IConversationStore>();
        conversations
            .Setup(store => store.ArchiveAsync(
                It.IsAny<ConversationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => archivedAt ??= sequence.Next())
            .Returns(Task.CompletedTask);

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = ConversationId.From("conv-abc")
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [action], conversationStore: conversations.Object);
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await scheduler.DeleteJobAsync(JobId.From("job-1"));
        await runTask;

        action.CancellationObservedAt.ShouldNotBeNull();
        archivedAt.ShouldNotBeNull();
        archivedAt!.Value.ShouldBeGreaterThan(action.CancellationObservedAt!.Value);
    }

    [Fact]
    public async Task DeleteJob_WithNoActiveRun_StillDeletesTheJob()
    {
        // The wait for observation must be conditional on there BEING a run. A delete for an idle
        // job must not block on a handshake that will never complete.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("test-action")]);

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJob_StillRemovesTheJobRow_WhenTheRunIgnoresCancellation()
    {
        // Fail-open on the watchdog. An action that swallows its token must not make the job
        // permanently undeletable - the operator's removal has to win regardless.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new UncooperativeAction("test-action");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));

        var scheduler = CreateScheduler(
            context.Store,
            [action],
            // A short grace so the watchdog elapses inside the test rather than the default wait.
            options: new CronOptions
            {
                Enabled = true,
                TickIntervalSeconds = 1,
                ActiveRunCancellationGraceSeconds = 1
            });

        var runTask = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.Started.Task;

        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();

        action.Release();
        await runTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IReadOnlyList<ICronAction> actions,
        ISessionStore? sessionStore = null,
        IConversationStore? conversationStore = null,
        ICronFailureAlertSink? alertSink = null,
        CronOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sessionStore ?? Mock.Of<ISessionStore>());
        services.AddSingleton(conversationStore ?? Mock.Of<IConversationStore>());
        if (alertSink is not null)
            services.AddSingleton(alertSink);
        var provider = services.BuildServiceProvider();

        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(
                options ?? new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 }),
            NullLogger<CronScheduler>.Instance);
    }

    /// <summary>Monotonic sequence stamper, so orderings are asserted on numbers rather than clocks.</summary>
    private sealed class OrderLog
    {
        private long _next;
        public long Next() => Interlocked.Increment(ref _next);
    }

    /// <summary>
    /// Blocks until its token is cancelled (or <see cref="Release"/> is called), capturing the exact
    /// token it was handed and the moment it observed cancellation.
    /// </summary>
    private sealed class BlockingAction(string actionType, OrderLog? order = null) : ICronAction
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ActionType => actionType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }
        public long? CancellationObservedAt { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            Started.TrySetResult();

            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());

            await Task.WhenAny(_release.Task, cancelled.Task).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                CancellationObservedAt = order?.Next();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Models an action that ignores its cancellation token entirely.</summary>
    private sealed class UncooperativeAction(string actionType) : ICronAction
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ActionType => actionType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
    }

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingAction(string actionType, string message) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class RecordingAlertSink : ICronFailureAlertSink
    {
        private readonly List<CronFailureAlert> _alerts = [];
        public IReadOnlyList<CronFailureAlert> Alerts
        {
            get { lock (_alerts) { return _alerts.ToList(); } }
        }

        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
        {
            lock (_alerts) { _alerts.Add(alert); }
            return Task.CompletedTask;
        }
    }

    /// <summary>Session store that stamps the sequence position of its first delete.</summary>
    private sealed class OrderedSessionStore(OrderLog order) : ISessionStore
    {
        private readonly List<GatewaySession> _sessions = [];

        public long? FirstDeleteAt { get; private set; }

        public void Seed(string agentId, string sessionId)
            => _sessions.Add(new GatewaySession
            {
                SessionId = SessionId.From(sessionId),
                AgentId = AgentId.From(agentId)
            });

        public Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            FirstDeleteAt ??= order.Next();
            _sessions.RemoveAll(s => s.SessionId == sessionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<GatewaySession> result = agentId is { } id
                ? _sessions.Where(s => s.AgentId == id).ToList()
                : _sessions.ToList();
            return Task.FromResult(result);
        }

        public Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_sessions.FirstOrDefault(s => s.SessionId == sessionId));
        public Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
