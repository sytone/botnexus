using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #4688: the scheduler loop awaited the WHOLE tick (including every due job's execution) before it
/// could tick again. A batch of long-running agent-prompt jobs therefore parked the loop: the due-scan
/// never ran, so NO job of any kind was dispatched until the slowest job in the batch finished.
///
/// These tests pin the dispatch/completion decoupling. The invariant under test is NOT "jobs run"
/// (the old code ran them eventually) but "a slow job cannot stop the NEXT tick from dispatching".
/// </summary>
public sealed class CronSchedulerTickStarvationTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The headline defect. One job that never completes must not prevent subsequent ticks from
    /// dispatching other due jobs. Under the blocking loop the fast job never runs at all.
    /// </summary>
    [Fact]
    public async Task SlowJob_DoesNotBlockTheNextTick_FromDispatchingOtherJobs()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var slow = new GatedAction("slow-action", release);
        var fast = new GatedAction("fast-action", null);

        await context.Store.CreateAsync(DueJob("slow-job", "slow-action"));
        var scheduler = CreateScheduler(context.Store, [slow, fast],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 5 });

        // Tick 1 dispatches the job that will never finish on its own.
        await InvokeProcessTickAsync(scheduler);
        (await slow.WaitForStartAsync(Settle)).ShouldBeTrue("the slow job should have been dispatched by tick 1");

        // A job becomes due AFTER the slow one is already in flight.
        await context.Store.CreateAsync(DueJob("fast-job", "fast-action"));

        // Tick 2 must still dispatch it. The blocking loop could never even reach this point.
        await InvokeProcessTickAsync(scheduler);

        (await fast.WaitForStartAsync(Settle)).ShouldBeTrue(
            "a job in flight must not prevent a later tick from dispatching a newly due job");

        release.Set();
        await slow.WaitForCompletionAsync(Settle);
        await DrainAsync(scheduler);
    }
    /// <summary>
    /// The precise regression: ProcessTickAsync must RETURN once jobs are dispatched, rather than
    /// awaiting their completion. This is what frees the loop to reach its Task.Delay and tick again.
    /// </summary>
    [Fact]
    public async Task ProcessTick_ReturnsWithoutWaitingForJobCompletion()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var slow = new GatedAction("slow-action", release);
        await context.Store.CreateAsync(DueJob("slow-job", "slow-action"));

        var scheduler = CreateScheduler(context.Store, [slow],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 5 });

        var tick = InvokeProcessTickRawAsync(scheduler);
        var returnedFirst = await ReturnsWithinAsync(tick, Settle);

        returnedFirst.ShouldBeTrue("the tick must not block on a job that has not completed");
        slow.IsComplete.ShouldBeFalse("the tick returned, but the job should still be in flight");

        release.Set();
        await slow.WaitForCompletionAsync(Settle);
        await DrainAsync(scheduler);
    }

    /// <summary>
    /// Corner case - the cap must still hold once dispatch is non-blocking. Decoupling must not be
    /// achieved by simply abandoning the #2670 aggregate bound and fanning out without limit.
    /// </summary>
    [Fact]
    public async Task NonBlockingDispatch_StillHonoursTheConcurrencyCap()
    {
        const int cap = 3;
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var action = new GatedAction("slow-action", release);

        for (var i = 0; i < 12; i++)
            await context.Store.CreateAsync(DueJob($"job-{i}", "slow-action"));

        var scheduler = CreateScheduler(context.Store, [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = cap });

        await InvokeProcessTickAsync(scheduler);
        await action.WaitForStartCountAsync(cap, Settle);

        // Every job is claimed synchronously by the tick; the rest are parked on the slot gate.
        scheduler.InFlightCount.ShouldBe(12, "all due jobs must be dispatched, the excess queued on the cap");

        release.Set();
        await action.WaitForCompletionCountAsync(12, Settle);
        action.CompletionCount.ShouldBe(12, "every due job must still run - nothing may be dropped");
        await DrainAsync(scheduler);

        // Peak is observed over the WHOLE run (every queued job has entered and left the action),
        // so any unbounded fan-out would have been recorded - no wall-clock settle window needed.
        action.PeakConcurrency.ShouldBeLessThanOrEqualTo(cap,
            $"peak {action.PeakConcurrency} exceeded cap {cap}; dispatch must stay bounded");
    }

    /// <summary>
    /// Corner case - a job whose action throws must not poison the loop or the slot it occupied.
    /// </summary>
    [Fact]
    public async Task FaultedJob_DoesNotBlockSubsequentDispatch()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var faulting = new FaultingAction("boom-action");
        var healthy = new GatedAction("fast-action", null);

        await context.Store.CreateAsync(DueJob("boom-job", "boom-action"));
        var scheduler = CreateScheduler(context.Store, [faulting, healthy],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 1 });

        await InvokeProcessTickAsync(scheduler);
        await faulting.WaitForStartAsync(Settle);

        await context.Store.CreateAsync(DueJob("fast-job", "fast-action"));
        await InvokeProcessTickAsync(scheduler);

        (await healthy.WaitForStartAsync(Settle)).ShouldBeTrue(
            "a faulted job must release its slot and never wedge the dispatcher");
        await DrainAsync(scheduler);
    }

    /// <summary>
    /// Corner case - the per-job lock (#2670) must still serialise repeat runs of ONE job even
    /// though dispatch no longer blocks. Otherwise a slow job would be re-dispatched every tick
    /// and pile up concurrent copies of itself.
    /// </summary>
    [Fact]
    public async Task SameJob_IsNotRedispatchedWhileStillInFlight()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var action = new GatedAction("slow-action", release);
        await context.Store.CreateAsync(DueJob("slow-job", "slow-action"));

        var scheduler = CreateScheduler(context.Store, [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 5 });

        await InvokeProcessTickAsync(scheduler);
        await action.WaitForStartAsync(Settle);

        // Several more ticks while the job is still running.
        //
        // NextRunAt is advanced at dispatch, so a plain re-tick would find the job NOT due and never
        // reach the _inFlight guard at all - the guard would look covered while being untested. Force
        // the job back to due before each tick so the guard is the ONLY thing that can stop a
        // re-dispatch. (Found by mutation: without this, disabling the _inFlight claim still passed.)
        for (var i = 0; i < 3; i++)
        {
            await context.Store.SetNextRunAtAsync(JobId.From("slow-job"), DateTimeOffset.UtcNow.AddMinutes(-1));
            await InvokeProcessTickAsync(scheduler);
        }

        // NOTE: asserting PeakConcurrency here would be TAUTOLOGICAL. Same-job entry into the action
        // is already serialised by the pre-existing per-job _jobLocks semaphore, so peak stays 1 even
        // with _inFlight deleted - the redundant dispatches would simply queue on the job lock.
        // StartCount and InFlightCount bind the _inFlight claim itself: without it, the later ticks
        // dispatch additional runs that each stamp a Running row before blocking on the job lock,
        // which is precisely the orphaned-run garbage #2410 exists to reap.
        // The claim is taken synchronously inside the tick, so this is exact once the ticks returned.
        scheduler.InFlightCount.ShouldBe(1, "the in-flight claim must hold exactly one entry for the running job");

        // Any redundant dispatch is a tracked in-flight task, so the drain waits for it to stamp its
        // run row and enter the action. After the drain the counts below are final - no sleep needed.
        release.Set();
        await action.WaitForCompletionAsync(Settle);
        await DrainAsync(scheduler);

        action.StartCount.ShouldBe(1, "a job already in flight must not be dispatched again by a later tick");

        // THE binding assertion. StartCount and InFlightCount are both absorbed by other mechanisms:
        // the pre-existing per-job _jobLocks serialises entry into the action, and _inFlight is a
        // dictionary so a duplicate claim dedupes to one key. Run rows are not deduped. Without the
        // claim, each redundant dispatch reaches RecordRunStartAsync - which stamps a Running row
        // BEFORE the job lock is acquired - leaving extra orphaned Running rows for #2410 to reap.
        // Proven necessary by mutation: with the _inFlight guard neutered this is the assertion that
        // goes red.
        var runs = await context.Store.GetRunHistoryAsync(JobId.From("slow-job"), limit: 50);
        runs.Count.ShouldBe(1,
            $"expected exactly one run row, found {runs.Count} - a job in flight was dispatched again "
            + "and stamped duplicate Running rows");

        scheduler.InFlightCount.ShouldBe(0, "the claim must be released once the job completes");
    }

    /// <summary>
    /// Dispatched jobs are detached from the tick, so the test must wait for them to finish before
    /// the store is disposed - otherwise a still-running job holds cron.db open and teardown fails.
    /// This also exercises the production shutdown drain added for #4688.
    /// </summary>
    private static async Task DrainAsync(CronScheduler scheduler)
    {
        using var cts = new CancellationTokenSource(Settle);
        await scheduler.StopAsync(cts.Token);
    }

    /// <summary>
    /// C1 (review): the dispatch gate must be fixed for the process lifetime. An earlier revision
    /// minted a NEW full-capacity semaphore whenever the cap changed, so lowering the cap while jobs
    /// held permits on the old instance RAISED total concurrency, and an oscillating value made
    /// admission unbounded. #2670 is a ceiling on billed turns, so that is not an acceptable relaxation.
    /// </summary>
    [Fact]
    public async Task CapChange_DoesNotMintAdditionalSlots_AndNeverExceedsTheOriginalCap()
    {
        const int originalCap = 2;
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var action = new GatedAction("slow-action", release);

        for (var i = 0; i < originalCap; i++)
            await context.Store.CreateAsync(DueJob($"job-{i}", "slow-action"));

        var options = new MutableOptionsMonitor<CronOptions>(
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = originalCap });
        var scheduler = CreateScheduler(context.Store, [action], options);

        // Saturate the gate: originalCap jobs running, zero permits free.
        await InvokeProcessTickAsync(scheduler);
        await action.WaitForStartCountAsync(originalCap, Settle);

        // RAISE the cap, then make NEW jobs due. Raising is the binding direction: a rebuilt gate
        // would hand out 4 fresh permits while the original 2 are still held (peak 6). Lowering is
        // unobservable with a fixed gate, and jobs due BEFORE the change are already claimed into
        // _inFlight, so later ticks dispatch nothing and nothing can push against the ceiling -
        // that was the tautology in the previous version of this test (review B1).
        options.Set(new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 4 });
        for (var i = 0; i < 6; i++)
            await context.Store.CreateAsync(DueJob($"late-{i}", "slow-action"));

        await InvokeProcessTickAsync(scheduler);

        release.Set();
        await action.WaitForCompletionCountAsync(originalCap + 6, Settle);
        await DrainAsync(scheduler);

        // Peak spans the whole run, including the window where the late jobs pushed against the
        // gate while the original permits were still held, so no wall-clock settle is needed.
        action.PeakConcurrency.ShouldBeLessThanOrEqualTo(originalCap,
            $"peak {action.PeakConcurrency} exceeded the original cap {originalCap}; a cap change must "
            + "never widen the gate");
    }

    /// <summary>
    /// B2 (review): every other test reaches the scheduler through ProcessTickAsync reflection, so
    /// ExecuteAsync never runs, _executeTask is null, and base.StopAsync early-returns WITHOUT
    /// cancelling. That structurally hid the fact that dispatched jobs were running on the stopping
    /// token and were being cancelled rather than drained. This test starts the service for real so
    /// the production shutdown ordering is actually covered.
    /// </summary>
    [Fact]
    public async Task StartedService_DrainsInFlightJob_RatherThanCancellingIt()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var action = new GatedAction("slow-action", release);
        await context.Store.CreateAsync(DueJob("slow-job", "slow-action"));

        var scheduler = CreateScheduler(context.Store, [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 5 });

        await scheduler.StartAsync(CancellationToken.None);
        (await action.WaitForStartAsync(Settle)).ShouldBeTrue("the hosted loop should dispatch the due job");

        // Shut down while the job is mid-flight, releasing it just after so it can finish inside the
        // grace period. If dispatch ran on the stopping token this would abort instead of complete.
        // base.StopAsync cancels the stopping token synchronously before its first await, so by the
        // time StopAsync hands back its task a job running on that token has already observed it.
        var stop = scheduler.StopAsync(CancellationToken.None);
        release.Set();
        await stop;

        action.CompletionCount.ShouldBe(1,
            "an in-flight job must be allowed to finish during the shutdown grace period, not cancelled");
        action.WasCancelled.ShouldBeFalse("the dispatched job must not observe cancellation at shutdown");
    }

    /// <summary>
    /// C2 (review): the drain must terminate promptly rather than hot-spinning. WhenAll can return
    /// before the removal continuations have run, so a re-snapshotting loop that does not retire the
    /// completed set itself spins against already-finished tasks.
    /// </summary>
    [Fact]
    public async Task WaitForInFlight_CompletesPromptly_WithoutSpinning()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var action = new GatedAction("slow-action", release);

        for (var i = 0; i < 4; i++)
            await context.Store.CreateAsync(DueJob($"job-{i}", "slow-action"));

        var scheduler = CreateScheduler(context.Store, [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 4 });

        await InvokeProcessTickAsync(scheduler);
        await action.WaitForStartCountAsync(4, Settle);
        release.Set();

        using var cts = new CancellationTokenSource(Settle);
        await scheduler.WaitForInFlightAsync(cts.Token);

        scheduler.InFlightCount.ShouldBe(0, "the drain must not return while jobs are still claimed");
        action.CompletionCount.ShouldBe(4);
    }

    /// <summary>
    /// Changelog item 3 (review: "no test at all"): NextRunAt must be advanced at DISPATCH time, so a
    /// run that outlives its own interval is not seen as perpetually due by subsequent ticks.
    /// </summary>
    [Fact]
    public async Task NextRunAt_IsAdvancedAtDispatch_NotAtCompletion()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var action = new GatedAction("slow-action", release);
        await context.Store.CreateAsync(DueJob("slow-job", "slow-action"));

        var scheduler = CreateScheduler(context.Store, [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 5 });

        await InvokeProcessTickAsync(scheduler);
        await action.WaitForStartAsync(Settle);

        // The job is STILL RUNNING. Its NextRunAt must already point into the future.
        var job = await context.Store.GetAsync(JobId.From("slow-job"));
        job.ShouldNotBeNull();
        job!.NextRunAt.ShouldNotBeNull();
        job.NextRunAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow,
            "NextRunAt must be advanced when the job is dispatched, not when it finishes");

        release.Set();
        await action.WaitForCompletionAsync(Settle);
        await DrainAsync(scheduler);
    }

    /// <summary>
    /// CHARACTERISATION test, not a defect regression. Asserts the invariant that a run row always
    /// reaches a terminal state even when the scheduler is disposed mid-flight - deliberately
    /// mechanism-agnostic, so it survives removal of the Dispose guard and does NOT evidence a
    /// specific bug. (Review round 3 posited an ObjectDisposedException path here; a net10.0 probe
    /// disproved it - Register/CreateLinkedTokenSource no longer throw on a disposed source, and
    /// SemaphoreSlim.WaitAsync raises TaskCanceledException, which the abort path already finalises.
    /// The blocker was withdrawn in round 4.) Kept because a future change that genuinely does
    /// strand the row will go red here.
    /// </summary>
    [Fact]
    public async Task Dispose_DuringInFlightJob_RunRowReachesTerminalState()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var release = new ManualResetEventSlim(false);
        var action = new GatedAction("slow-action", release);
        await context.Store.CreateAsync(DueJob("slow-job", "slow-action"));

        var scheduler = CreateScheduler(context.Store, [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 5 });

        await InvokeProcessTickAsync(scheduler);
        (await action.WaitForStartAsync(Settle)).ShouldBeTrue();

        // Shut down with a token that is ALREADY cancelled, so the drain gives up immediately and
        // the job is still live when Dispose runs - the exact ordering that produced the ODE.
        using (var expired = new CancellationTokenSource())
        {
            await expired.CancelAsync();
            await scheduler.StopAsync(expired.Token);
        }

        scheduler.Dispose();

        // The job is still running and its token is now cancelled. Let it unwind.
        release.Set();

        // Whatever the outcome, the run must have reached a TERMINAL state. Still 'Running' means
        // the exception escaped every bookkeeping path.
        await TestAwait.EventuallyAsync(
            async () => (await context.Store.GetRunHistoryAsync(JobId.From("slow-job"), limit: 10))
                .Any(run => run.Status != "Running"),
            "the in-flight run to reach a terminal state after Dispose",
            Settle);
        var runs = await context.Store.GetRunHistoryAsync(JobId.From("slow-job"), limit: 10);
        runs.Count.ShouldBe(1);
        runs[0].Status.ShouldNotBe("Running",
            $"run was left in '{runs[0].Status}' - a job in flight at Dispose never reached a terminal state");
    }

    /// <summary>
    /// Found by the round-4 sweep: CancellationTokenSource.Token and Cancel() DID keep their
    /// ThrowIfDisposed behaviour on net10.0 (unlike Register/CreateLinkedTokenSource). So a second
    /// StopAsync after Dispose, or a tick racing a late Dispose, would fault on the disposed source.
    /// </summary>
    [Fact]
    public async Task StopAsync_AfterDispose_DoesNotThrowOnTheDisposedDispatchSource()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new GatedAction("fast-action", null);
        await context.Store.CreateAsync(DueJob("fast-job", "fast-action"));

        var scheduler = CreateScheduler(context.Store, [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 5 });

        await InvokeProcessTickAsync(scheduler);
        await action.WaitForCompletionAsync(Settle);

        await scheduler.StopAsync(CancellationToken.None);
        scheduler.Dispose();

        // The disposing path ran with no jobs outstanding, so _dispatchCts really is disposed here.
        // A second stop must still be safe - Cancel() on a disposed source throws ObjectDisposedException.
        await Should.NotThrowAsync(async () => await scheduler.StopAsync(CancellationToken.None));
    }

    /// <summary>
    /// Review M2: StopAsync can begin while the tick is awaiting SetNextRunAtAsync. The pre-claim
    /// _stopping check has already passed by then, so without a re-check after the await the tick
    /// dispatches a job after shutdown started - outside the drain, on a token about to be cancelled.
    /// The store hook flips _stopping at exactly that await, deterministically reproducing the race.
    /// </summary>
    [Fact]
    public async Task ShutdownBeginningDuringReschedule_DoesNotDispatch_AndReleasesTheClaim()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new GatedAction("fast-action", null);
        await context.Store.CreateAsync(DueJob("racing-job", "fast-action"));

        CronScheduler? scheduler = null;
        var store = new StopOnRescheduleCronStore(context.Store, () =>
        {
            var field = typeof(CronScheduler).GetField("_stopping", BindingFlags.NonPublic | BindingFlags.Instance);
            field.ShouldNotBeNull();
            field!.SetValue(scheduler, true);
        });
        scheduler = CreateScheduler(store, [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 5 });

        await InvokeProcessTickAsync(scheduler);

        // The claim is released synchronously inside the tick, so this is exact once it returned.
        scheduler.InFlightCount.ShouldBe(0, "the skipped job's in-flight claim must be released");

        // A wrongly dispatched run is a tracked in-flight task; the drain waits for it to start and
        // stamp its row, so the assertions below are final rather than racing a sleep.
        await DrainAsync(scheduler);

        action.StartCount.ShouldBe(0, "a job must not be dispatched once shutdown began during its reschedule");
        var runs = await context.Store.GetRunHistoryAsync(JobId.From("racing-job"), limit: 10);
        runs.Count.ShouldBe(0, "no run row may be stamped for a job skipped due to shutdown");
    }

    public enum QueuedMutation { Disable, Expire, Delete }

    /// <summary>
    /// #4731 review P1: a job parked on the concurrency gate is in _inFlight but not _activeRuns, so
    /// disable/delete could not reach it, and once a slot freed it ran the due-scan SNAPSHOT of a job
    /// that had since been disabled, expired or deleted. The queued waiter must re-read and
    /// revalidate (and be cancellable via the operator seam) so B's action never runs.
    /// </summary>
    [Theory]
    [InlineData(QueuedMutation.Disable)]
    [InlineData(QueuedMutation.Expire)]
    [InlineData(QueuedMutation.Delete)]
    public async Task CapQueuedJob_MutatedWhileQueued_NeverRuns(QueuedMutation mutation)
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var releaseA = new ManualResetEventSlim(false);
        var gatedA = new GatedAction("a-action", releaseA);
        var b = new GatedAction("b-action", null);
        var runStartsForB = 0;
        var store = new StopOnRescheduleCronStore(context.Store, () => { }, id =>
        {
            if (id == JobId.From("job-b"))
                Interlocked.Increment(ref runStartsForB);
        });

        await context.Store.CreateAsync(DueJob("job-a", "a-action"));
        var scheduler = CreateScheduler(store, [gatedA, b],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 1 });

        // A takes the only slot and holds it.
        await InvokeProcessTickAsync(scheduler);
        (await gatedA.WaitForStartAsync(Settle)).ShouldBeTrue();

        // B becomes due and is dispatched into the cap queue.
        await context.Store.CreateAsync(DueJob("job-b", "b-action"));
        await InvokeProcessTickAsync(scheduler);
        scheduler.InFlightCount.ShouldBe(2, "B must be claimed and parked on the cap gate");

        var jobB = JobId.From("job-b");
        var existing = await context.Store.GetAsync(jobB);
        existing.ShouldNotBeNull();
        switch (mutation)
        {
            case QueuedMutation.Disable:
                await context.Store.UpdateDefinitionAsync(existing with { Enabled = false });
                await scheduler.CancelActiveRunAsync(jobB);
                break;
            case QueuedMutation.Expire:
                await context.Store.UpdateDefinitionAsync(existing with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
                break;
            case QueuedMutation.Delete:
                await scheduler.DeleteJobAsync(jobB);
                break;
        }

        // Free the slot so a stale waiter WOULD now run B.
        releaseA.Set();
        (await gatedA.WaitForCompletionAsync(Settle)).ShouldBeTrue();
        await DrainAsync(scheduler);

        b.StartCount.ShouldBe(0, $"B was mutated ({mutation}) while queued for a slot and must never run");
        // Binding for Delete: the stale dispatch used to fall back to the captured definition and
        // try to stamp a run for a job that no longer exists (it then failed in the store rather
        // than in the action, which StartCount alone cannot see).
        Volatile.Read(ref runStartsForB).ShouldBe(0,
            $"the queued dispatch of B ({mutation}) must not start a run from the stale captured definition");
        if (mutation != QueuedMutation.Delete)
        {
            var runs = await context.Store.GetRunHistoryAsync(jobB, limit: 10);
            runs.Count.ShouldBe(0, "no run row may be stamped for a queued fire that was invalidated");
        }
    }

/// <summary>#4731 review P2: Dispose without StopAsync must cancel dispatched runs.</summary>
    [Fact]
    public async Task Dispose_WithoutStop_CancelsDispatchedRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        using var neverRelease = new ManualResetEventSlim(false);
        var gatedA = new GatedAction("a-action", neverRelease);
        await context.Store.CreateAsync(DueJob("job-a", "a-action"));
        var scheduler = CreateScheduler(context.Store, [gatedA],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 1 });

        await InvokeProcessTickAsync(scheduler);
        (await gatedA.WaitForStartAsync(Settle)).ShouldBeTrue();

        scheduler.Dispose();

        (await gatedA.WaitForCancelledAsync(Settle)).ShouldBeTrue("Dispose must cancel the dispatch token");
        await DrainAsync(scheduler);
    }
    /// <summary>Pass-through store that runs a hook after SetNextRunAtAsync completes.</summary>
    private sealed class StopOnRescheduleCronStore(ICronStore inner, Action onReschedule, Action<JobId>? onRunStart = null) : ICronStore
    {
        public async Task SetNextRunAtAsync(JobId jobId, DateTimeOffset? nextRunAt, CancellationToken ct = default)
        {
            await inner.SetNextRunAtAsync(jobId, nextRunAt, ct);
            onReschedule();
        }

        public Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default)
        {
            onRunStart?.Invoke(jobId);
            return inner.RecordRunStartAsync(jobId, ct);
        }
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default) => inner.CreateAsync(job, ct);
        public Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default) => inner.GetAsync(jobId, ct);
        public Task<IReadOnlyList<CronJob>> ListAsync(AgentId? agentId = null, CancellationToken ct = default) => inner.ListAsync(agentId, ct);
        public Task<CronJob?> UpdateDefinitionAsync(CronJob job, CronJobOwnershipExpectation? expectedOwnership = null, CancellationToken ct = default)
            => inner.UpdateDefinitionAsync(job, expectedOwnership, ct);
        public Task SetBackoffUntilAsync(JobId jobId, DateTimeOffset? backoffUntil, CancellationToken ct = default) => inner.SetBackoffUntilAsync(jobId, backoffUntil, ct);
        public Task DeleteAsync(JobId jobId, CancellationToken ct = default) => inner.DeleteAsync(jobId, ct);
        public Task RecordRunFinalizationAsync(JobId jobId, DateTimeOffset lastRunAt, string lastRunStatus, string? lastRunError, CancellationToken ct = default)
            => inner.RecordRunFinalizationAsync(jobId, lastRunAt, lastRunStatus, lastRunError, ct);
        public Task RecordRunCompleteAsync(RunId runId, string status, string? error = null, SessionId? sessionId = null, CronRunCost? cost = null, CancellationToken ct = default)
            => inner.RecordRunCompleteAsync(runId, status, error, sessionId, cost, ct);
        public Task<IReadOnlyList<CronJobCostRollup>> GetJobCostRollupsAsync(IReadOnlyCollection<JobId> jobIds, int windowDays = 7, CancellationToken ct = default)
            => inner.GetJobCostRollupsAsync(jobIds, windowDays, ct);
        public Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(JobId jobId, int limit = 20, CancellationToken ct = default) => inner.GetRunHistoryAsync(jobId, limit, ct);
        public Task<IReadOnlyList<CronRun>> GetRecentRunsAsync(IReadOnlyCollection<JobId> jobIds, IReadOnlyCollection<string>? statuses = null, int limit = 20, CancellationToken ct = default)
            => inner.GetRecentRunsAsync(jobIds, statuses, limit, ct);
        public Task<ConversationId?> TrySetConversationIdAsync(JobId jobId, ConversationId conversationId, CancellationToken ct = default)
            => inner.TrySetConversationIdAsync(jobId, conversationId, ct);
        public Task<int> PurgeRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) => inner.PurgeRunsOlderThanAsync(cutoff, ct);
        public Task<IReadOnlyList<CronRun>> ListRunningRunsAsync(CancellationToken ct = default) => inner.ListRunningRunsAsync(ct);
        public Task<bool> TryRecordMissedRunAsync(JobId jobId, DateTimeOffset scheduledOccurrenceUtc, CancellationToken ct = default)
            => inner.TryRecordMissedRunAsync(jobId, scheduledOccurrenceUtc, ct);
    }

    private static CronJob DueJob(string id, string actionType)
        => CronStoreTestContext.CreateJob(id, actionType: actionType) with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        CronOptions options)
        => CreateScheduler(store, actions, new StaticOptionsMonitor<CronOptions>(options));

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        IOptionsMonitor<CronOptions> options)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            actions,
            scopeFactory,
            options,
            NullLogger<CronScheduler>.Instance);
    }

    /// <summary>
    /// Bounded tick invocation. The defect under test BLOCKS rather than throwing, so an unbounded
    /// await would hang the test run instead of producing a readable failure. The timeout converts
    /// "the tick never returned" into an explicit, diagnosable assertion.
    /// </summary>
    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var tick = InvokeProcessTickRawAsync(scheduler);
        if (!await ReturnsWithinAsync(tick, Settle))
        {
            throw new Shouldly.ShouldAssertException(
                $"ProcessTickAsync did not return within {Settle.TotalSeconds:0}s. The tick is awaiting job " +
                "completion, so the scheduler loop cannot tick again and every job is starved (#4688).");
        }

        await tick;
    }

    /// <summary>
    /// True when <paramref name="task"/> finishes inside <paramref name="window"/>. Bounds an await on
    /// code under test that is expected to return, without racing it against a wall-clock sleep.
    /// </summary>
    private static async Task<bool> ReturnsWithinAsync(Task task, TimeSpan window)
    {
        try
        {
            await task.WaitAsync(window);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Unbounded invocation, for the test that measures the tick's own return behaviour.</summary>
    private static Task InvokeProcessTickRawAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        return task!;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// Review C1 noted that StaticOptionsMonitor made the cap-change branch unreachable from the
    /// suite. This one actually changes.
    /// </summary>
    private sealed class MutableOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        private T _current = initial;
        public T CurrentValue => _current;
        public T Get(string? name) => _current;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
        public void Set(T value) => _current = value;
    }

    /// <summary>
    /// An action that blocks until explicitly released, so "in flight" is a deterministic state
    /// rather than a timing race. A null gate means "complete immediately".
    /// </summary>
    private sealed class GatedAction(string actionType, ManualResetEventSlim? gate) : ICronAction
    {
        private readonly SemaphoreSlim _started = new(0);
        private readonly SemaphoreSlim _completed = new(0);
        private readonly SemaphoreSlim _cancelledSignal = new(0);
        private int _current;
        private int _peak;
        private int _startCount;
        private int _completionCount;
        private int _cancelled;

        public string ActionType => actionType;
        public int PeakConcurrency => Volatile.Read(ref _peak);
        public int StartCount => Volatile.Read(ref _startCount);
        public int CompletionCount => Volatile.Read(ref _completionCount);
        public bool IsComplete => Volatile.Read(ref _completionCount) > 0;
        public bool WasCancelled => Volatile.Read(ref _cancelled) > 0;

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _current);
            int observedPeak;
            while (now > (observedPeak = Volatile.Read(ref _peak)))
            {
                if (Interlocked.CompareExchange(ref _peak, now, observedPeak) == observedPeak)
                    break;
            }

            Interlocked.Increment(ref _startCount);
            _started.Release();
            try
            {
                if (gate is not null)
                    await Task.Run(() => gate.Wait(cancellationToken), cancellationToken).ConfigureAwait(false);

                Interlocked.Increment(ref _completionCount);
                _completed.Release();
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                _cancelledSignal.Release();
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        public Task<bool> WaitForStartAsync(TimeSpan timeout) => _started.WaitAsync(timeout);
        public Task<bool> WaitForCompletionAsync(TimeSpan timeout) => _completed.WaitAsync(timeout);
        public Task<bool> WaitForCancelledAsync(TimeSpan timeout) => _cancelledSignal.WaitAsync(timeout);

        public async Task WaitForStartCountAsync(int count, TimeSpan timeout)
        {
            for (var i = 0; i < count; i++)
                (await _started.WaitAsync(timeout)).ShouldBeTrue($"expected at least {count} starts");
        }

        public async Task WaitForCompletionCountAsync(int count, TimeSpan timeout)
        {
            for (var i = 0; i < count; i++)
                (await _completed.WaitAsync(timeout)).ShouldBeTrue($"expected {count} completions");
        }
    }

    private sealed class FaultingAction(string actionType) : ICronAction
    {
        private readonly SemaphoreSlim _started = new(0);
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            _started.Release();
            throw new InvalidOperationException("Simulated action failure.");
        }

        public Task<bool> WaitForStartAsync(TimeSpan timeout) => _started.WaitAsync(timeout);
    }
}
