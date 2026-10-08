using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2410: runs stamped <c>running</c> that never receive a terminal write (process kill, host
/// crash, OOM, power loss) were previously immune to both completion and retention pruning.
/// These tests pin the observable outcome of the reaper: the run row becomes
/// <see cref="CronRunStatus.Error"/> with the orphan reason, and is subsequently prunable.
/// </summary>
public sealed class CronOrphanedRunReaperTests
{
    [Fact]
    public async Task ReapOrphanedRunsAsync_MarksStaleRunningRunAsErrorWithOrphanReason()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(-48));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var reapedRun = history.ShouldHaveSingleItem();
        reapedRun.Status.ShouldBe(CronRunStatus.Error);
        reapedRun.Error.ShouldNotBeNull();
        reapedRun.Error!.ShouldContain("orphaned");
        reapedRun.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_PreviousPlannedShutdown_TerminalizesFreshRunImmediately()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        _ = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var scheduler = CreateScheduler(
            context.Store,
            shutdownState: new PlannedShutdownState(Current: false, Previous: true));

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(1);
        var terminal = (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).ShouldHaveSingleItem();
        terminal.Status.ShouldBe(CronRunStatus.Error);
        terminal.Error.ShouldBe(CronScheduler.PlannedRestartReason);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_ReapsFutureDatedStartedAtBeyondBound()
    {
        // A future-dated started_at (clock skew, restored DB, forced run) must also be reaped.
        // A naive (now - startedAt) > bound comparison yields a negative span and silently skips
        // the row forever -- this is the exact blind spot #2410 fixes via Math.Abs.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(48));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var reapedRun = history.ShouldHaveSingleItem();
        reapedRun.Status.ShouldBe(CronRunStatus.Error);
        reapedRun.Error!.ShouldContain("orphaned");
        reapedRun.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_LeavesInFlightRunWithinBoundUntouched()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        _ = await context.Store.RecordRunStartAsync(JobId.From("job-1"));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(0);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Running);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_LeavesFutureDatedRunWithinBoundUntouched()
    {
        // Small forward skew must not be treated as an orphan: Math.Abs widens the window
        // symmetrically, it does not make every future-dated run reapable.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddMinutes(5));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(0);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Running);
    }

    [Fact]
    public async Task ReapedRun_BecomesPrunableByRetention()
    {
        // The whole point of #2410: a stuck 'running' row is immune to PurgeRunsOlderThanAsync.
        // After reaping it must be a terminal row that retention can delete.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-90));

        // Before reaping: retention cannot touch it.
        (await context.Store.PurgeRunsOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-30))).ShouldBe(0);

        var scheduler = CreateScheduler(context.Store);
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);

        // The reaper stamps completed_at = now, so age the row and prune.
        await SetRunCompletedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-60));
        var purged = await context.Store.PurgeRunsOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-30));

        purged.ShouldBe(1);
        (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_ClearsStuckRunningLastRunStatusOnJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(-48));

        var scheduler = CreateScheduler(context.Store);
        await scheduler.ReapOrphanedRunsAsync();

        var job = await context.Store.GetAsync(JobId.From("job-1"));
        job.ShouldNotBeNull();
        job!.LastRunStatus.ShouldBe(CronRunStatus.Error);
        job.LastRunError.ShouldNotBeNull();
        job.LastRunError!.ShouldContain("orphaned");
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_DoesNotTouchTerminalRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run.Id, CronRunStatus.Ok);
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-90));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(0);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var only = history.ShouldHaveSingleItem();
        only.Status.ShouldBe(CronRunStatus.Ok);
        only.Error.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessTick_ReapsOrphanedRuns()
    {
        // The reaper must be wired into the periodic scheduler loop, not only startup.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(-48));

        var scheduler = CreateScheduler(context.Store);
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;

        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Error);
    }

    [Fact]
    public async Task ListRunningRunsAsync_ReturnsOnlyNonTerminalRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var terminal = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(terminal.Id, CronRunStatus.Ok);
        var inFlight = await context.Store.RecordRunStartAsync(JobId.From("job-1"));

        var running = await context.Store.ListRunningRunsAsync();

        running.ShouldHaveSingleItem().Id.Value.ShouldBe(inFlight.Id.Value);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_SealedOwnerSessionWithoutActiveExecutor_IsTerminalImmediately()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var sessionId = SessionId.From("cron:job-1:sealed");
        await context.Store.RecordRunSessionAsync(run.Id, sessionId);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(store => store.GetAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = sessionId,
                AgentId = AgentId.From("agent-a"),
                Status = SessionStatus.Sealed
            });
        var scheduler = CreateScheduler(context.Store, sessions.Object);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(1);
        var terminal = (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).ShouldHaveSingleItem();
        terminal.Status.ShouldBe(CronRunStatus.Error);
        terminal.Error.ShouldNotBeNull();
        terminal.Error.ShouldContain("sealed");
    }

    [Fact]
    public async Task GetRunHealthAsync_RunningRow_ReportsAgeOwnerSessionStateAndExecutorOwnership()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var sessionId = SessionId.From("cron:job-1:sealed");
        await context.Store.RecordRunSessionAsync(run.Id, sessionId);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(store => store.GetAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = sessionId,
                AgentId = AgentId.From("agent-a"),
                Status = SessionStatus.Sealed
            });
        var scheduler = CreateScheduler(context.Store, sessions.Object);
        var persistedRunning = (await context.Store.ListRunningRunsAsync()).ShouldHaveSingleItem();

        var health = (await scheduler.GetRunHealthAsync([persistedRunning])).ShouldHaveSingleItem();

        health.RunningAge.ShouldNotBeNull();
        health.RunningAge.Value.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        health.OwnerSessionState.ShouldBe("sealed");
        health.HasActiveExecutor.ShouldBeFalse();
    }

    [Fact]
    public async Task RunActionAsync_RecordsOwnerSessionBeforeTheActionCompletes()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "session-action"));
        var action = new SessionHoldingAction();
        var scheduler = CreateScheduler(context.Store, action: action);

        var execution = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.SessionRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = (await context.Store.ListRunningRunsAsync()).ShouldHaveSingleItem();
        running.SessionId.ShouldBe(action.SessionId);

        action.Release.TrySetResult();
        (await execution.WaitAsync(TimeSpan.FromSeconds(5))).Status.ShouldBe(CronRunStatus.Ok);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_InitialReadFailure_RetryKeepsPlannedRestartClassification()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var store = CreateReaperStore(context.Store);
        store.SetupSequence(s => s.ListRunningRunsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("initial read failed"))
            .ReturnsAsync(await context.Store.ListRunningRunsAsync());
        var scheduler = CreateScheduler(store.Object, shutdownState: new PlannedShutdownState(false, true));

        await Should.ThrowAsync<IOException>(() => scheduler.ReapOrphanedRunsAsync());
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);
        var terminal = (await context.Store.GetRunHistoryAsync(run.JobId)).ShouldHaveSingleItem();
        terminal.Error.ShouldBe(CronScheduler.PlannedRestartReason);
        terminal.Status.ShouldBe(CronRunStatus.Error);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_PartialTerminalWriteFailure_RetryKeepsRemainingCohortAndExcludesNewRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var first = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var second = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var store = CreateReaperStore(context.Store);
        var writes = 0;
        store.Setup(s => s.RecordRunCompleteAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<SessionId?>(), It.IsAny<CronRunCost?>(), It.IsAny<CancellationToken>()))
            .Returns((RunId id, string status, string? error, SessionId? session, CronRunCost? cost, CancellationToken ct) =>
                ++writes == 2 ? Task.FromException(new IOException("second terminal write failed"))
                    : context.Store.RecordRunCompleteAsync(id, status, error, session, cost, ct));
        var scheduler = CreateScheduler(store.Object, shutdownState: new PlannedShutdownState(false, true));

        await Should.ThrowAsync<IOException>(() => scheduler.ReapOrphanedRunsAsync());
        (await context.Store.ListRunningRunsAsync()).ShouldHaveSingleItem();
        var newlyCreated = await context.Store.RecordRunStartAsync(first.JobId);
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(first.JobId);
        history.Single(r => r.Id == first.Id).Error.ShouldBe(CronScheduler.PlannedRestartReason);
        history.Single(r => r.Id == second.Id).Error.ShouldBe(CronScheduler.PlannedRestartReason);
        history.Single(r => r.Id == newlyCreated.Id).Status.ShouldBe(CronRunStatus.Running);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_FinalizationFailure_RetryRepairsJobAfterRunIsTerminal()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunFinalizationAsync(run.JobId, run.StartedAt, CronRunStatus.Running, null);
        var store = CreateReaperStore(context.Store);
        var finalizations = 0;
        store.Setup(s => s.RecordRunFinalizationAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((JobId id, DateTimeOffset at, string status, string? error, CancellationToken ct) =>
                ++finalizations == 1 ? Task.FromException(new IOException("finalization failed"))
                    : context.Store.RecordRunFinalizationAsync(id, at, status, error, ct));
        var scheduler = CreateScheduler(store.Object, shutdownState: new PlannedShutdownState(false, true));

        await Should.ThrowAsync<IOException>(() => scheduler.ReapOrphanedRunsAsync());
        (await context.Store.ListRunningRunsAsync()).ShouldBeEmpty();
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);
        var job = await context.Store.GetAsync(run.JobId);
        job.ShouldNotBeNull();
        job.LastRunStatus.ShouldBe(CronRunStatus.Error);
        job.LastRunError.ShouldBe(CronScheduler.PlannedRestartReason);
        store.Verify(s => s.RecordRunCompleteAsync(run.Id, CronRunStatus.Error, CronScheduler.PlannedRestartReason,
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_InitialReadFailure_ExcludesLocallyAdmittedRunAfterExecutorLeaves()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2", actionType: "session-action"));
        var previous = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var store = CreateReaperStore(context.Store);
        var reads = 0;
        store.Setup(s => s.ListRunningRunsAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => ++reads == 1
                ? Task.FromException<IReadOnlyList<CronRun>>(new IOException("initial read failed"))
                : context.Store.ListRunningRunsAsync(ct));
        // Model a current-process terminal write that did not persist: after its executor leaves,
        // the row is still running, but it is not part of the previous process's restart cohort.
        store.Setup(s => s.RecordRunCompleteAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<SessionId?>(), It.IsAny<CronRunCost?>(), It.IsAny<CancellationToken>()))
            .Returns((RunId id, string status, string? error, SessionId? session, CronRunCost? cost, CancellationToken ct) =>
                id == previous.Id ? context.Store.RecordRunCompleteAsync(id, status, error, session, cost, ct)
                    : Task.CompletedTask);
        var action = new SessionHoldingAction();
        var scheduler = CreateScheduler(store.Object, action: action, shutdownState: new PlannedShutdownState(false, true));

        await Should.ThrowAsync<IOException>(() => scheduler.ReapOrphanedRunsAsync());
        var execution = scheduler.RunNowAsync(JobId.From("job-2"));
        await action.SessionRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        action.Release.TrySetResult();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ActiveRunCount.ShouldBe(0);
        // Equal/backdated timestamps cannot defeat current-process identity exclusion.
        var locallyAdmitted = (await context.Store.ListRunningRunsAsync())
            .Single(r => r.JobId == JobId.From("job-2"));
        await SetRunStartedAt(context.DbPath, locallyAdmitted.Id, previous.StartedAt);
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);
        (await context.Store.GetRunHistoryAsync(previous.JobId)).ShouldHaveSingleItem().Error
            .ShouldBe(CronScheduler.PlannedRestartReason);
        (await context.Store.GetRunHistoryAsync(JobId.From("job-2"))).ShouldHaveSingleItem().Status
            .ShouldBe(CronRunStatus.Running);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_PreviousPlannedShutdown_ProtectsActiveExecutorEvenWithTerminalOwner()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "session-action"));
        var action = new SessionHoldingAction();
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(action.SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = action.SessionId,
                AgentId = AgentId.From("agent-a"),
                Status = SessionStatus.Sealed
            });
        var scheduler = CreateScheduler(context.Store, sessions.Object, action, new PlannedShutdownState(false, true));
        var execution = scheduler.RunNowAsync(JobId.From("job-1"));
        await action.SessionRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(0);
            (await context.Store.ListRunningRunsAsync()).ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Running);
        }
        finally
        {
            action.Release.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_ShutdownClassifiedAfterConstruction_UsesLateClassification()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        _ = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var state = new Mock<IPlannedShutdownState>();
        var scheduler = CreateScheduler(context.Store, shutdownState: state.Object);
        state.SetupGet(s => s.PreviousShutdownWasPlanned).Returns(true);

        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);
        (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).ShouldHaveSingleItem().Error
            .ShouldBe(CronScheduler.PlannedRestartReason);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_InitialReadFailure_ExcludesExternalRunCreatedAfterStartupBoundary()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var previous = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var boundary = DateTimeOffset.UtcNow;
        await SetRunStartedAt(context.DbPath, previous.Id, boundary.AddMinutes(-1));
        var store = CreateReaperStore(context.Store);
        var reads = 0;
        store.Setup(s => s.ListRunningRunsAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => ++reads == 1
                ? Task.FromException<IReadOnlyList<CronRun>>(new IOException("initial read failed"))
                : context.Store.ListRunningRunsAsync(ct));
        var scheduler = CreateScheduler(store.Object, shutdownState: new PlannedShutdownState(false, true),
            timeProvider: new FixedTimeProvider(boundary));

        await Should.ThrowAsync<IOException>(() => scheduler.ReapOrphanedRunsAsync());
        var newRun = await context.Store.RecordRunStartAsync(previous.JobId);
        await SetRunStartedAt(context.DbPath, newRun.Id, boundary.AddSeconds(1));
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(previous.JobId);
        history.Single(r => r.Id == previous.Id).Error.ShouldBe(CronScheduler.PlannedRestartReason);
        history.Single(r => r.Id == newRun.Id).Status.ShouldBe(CronRunStatus.Running);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_FinalizationRetry_DoesNotRegressNewerRunningOccurrence()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var previous = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        var store = CreateReaperStore(context.Store);
        store.Setup(s => s.RecordRunFinalizationAsync(previous.JobId, previous.StartedAt,
                CronRunStatus.Error, CronScheduler.PlannedRestartReason, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("finalization failed"));
        var scheduler = CreateScheduler(store.Object, shutdownState: new PlannedShutdownState(false, true));

        await Should.ThrowAsync<IOException>(() => scheduler.ReapOrphanedRunsAsync());
        var newer = await context.Store.RecordRunStartAsync(previous.JobId);
        await context.Store.RecordRunFinalizationAsync(newer.JobId, previous.StartedAt.AddSeconds(1),
            CronRunStatus.Running, null);
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);
        var job = await context.Store.GetAsync(previous.JobId);
        job.ShouldNotBeNull();
        job.LastRunStatus.ShouldBe(CronRunStatus.Running);
        job.LastRunAt.ShouldBe(previous.StartedAt.AddSeconds(1));
        (await context.Store.ListRunningRunsAsync()).ShouldHaveSingleItem().Id.ShouldBe(newer.Id);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Mock<ICronStore> CreateReaperStore(ICronStore inner)
    {
        var store = new Mock<ICronStore>(MockBehavior.Strict);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => inner.InitializeAsync(ct));
        store.Setup(s => s.GetAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .Returns((JobId id, CancellationToken ct) => inner.GetAsync(id, ct));
        store.Setup(s => s.ListRunningRunsAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => inner.ListRunningRunsAsync(ct));
        store.Setup(s => s.RecordRunStartAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .Returns((JobId id, CancellationToken ct) => inner.RecordRunStartAsync(id, ct));
        store.Setup(s => s.RecordRunSessionAsync(It.IsAny<RunId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns((RunId id, SessionId session, CancellationToken ct) => inner.RecordRunSessionAsync(id, session, ct));
        store.Setup(s => s.RecordRunCompleteAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<SessionId?>(), It.IsAny<CronRunCost?>(), It.IsAny<CancellationToken>()))
            .Returns((RunId id, string status, string? error, SessionId? session, CronRunCost? cost, CancellationToken ct) =>
                inner.RecordRunCompleteAsync(id, status, error, session, cost, ct));
        store.Setup(s => s.RecordRunFinalizationAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((JobId id, DateTimeOffset at, string status, string? error, CancellationToken ct) =>
                inner.RecordRunFinalizationAsync(id, at, status, error, ct));
        return store;
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        ISessionStore? sessionStore = null,
        ICronAction? action = null,
        IPlannedShutdownState? shutdownState = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        if (sessionStore is not null)
            services.AddSingleton(sessionStore);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            action is null ? [] : [action],
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(new CronOptions
            {
                Enabled = true,
                TickIntervalSeconds = 1,
                OrphanedRunThresholdSeconds = 3600
            }),
            NullLogger<CronScheduler>.Instance,
            timeProvider: timeProvider,
            plannedShutdownState: shutdownState);
    }

    private sealed class SessionHoldingAction : ICronAction
    {
        public string ActionType => "session-action";
        public SessionId SessionId { get; } = SessionId.From("cron:job-1:running");
        public TaskCompletionSource SessionRecorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            await context.RecordSessionIdAsync(SessionId, cancellationToken);
            SessionRecorded.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private static async Task SetRunStartedAt(string dbPath, RunId runId, DateTimeOffset value)
        => await ExecuteAsync(dbPath, "UPDATE cron_runs SET started_at = $value WHERE id = $runId", value, runId);

    private static async Task SetRunCompletedAt(string dbPath, RunId runId, DateTimeOffset value)
        => await ExecuteAsync(dbPath, "UPDATE cron_runs SET completed_at = $value WHERE id = $runId", value, runId);

    private static async Task ExecuteAsync(string dbPath, string sql, DateTimeOffset value, RunId runId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value.ToString("O"));
        command.Parameters.AddWithValue("$runId", runId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record PlannedShutdownState(bool Current, bool Previous) : IPlannedShutdownState
    {
        public bool CurrentShutdownIsPlanned => Current;
        public bool PreviousShutdownWasPlanned => Previous;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
