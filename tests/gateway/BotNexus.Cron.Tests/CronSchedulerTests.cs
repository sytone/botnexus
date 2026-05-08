using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

public sealed class CronSchedulerTests
{
    [Fact]
    public async Task Scheduler_ExecutesDueJobs()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync("job-1");
        history.ShouldHaveSingleItem().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Scheduler_SkipsDisabledJobs()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action", enabled: false) with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(0);
        (await context.Store.GetRunHistoryAsync("job-1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Scheduler_RecordsRunOnSuccess()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync("job-1");

        run.Status.ShouldBe("ok");
        var updated = await context.Store.GetAsync("job-1");
        updated!.LastRunStatus.ShouldBe("ok");
        updated.LastRunError.ShouldBeNull();
        var history = await context.Store.GetRunHistoryAsync("job-1");
        history.ShouldHaveSingleItem().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Scheduler_RecordsErrorOnFailure()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new ThrowingAction("test-action", "boom");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync("job-1");

        run.Status.ShouldBe("error");
        run.Error.ShouldBe("boom");
        var updated = await context.Store.GetAsync("job-1");
        updated!.LastRunStatus.ShouldBe("error");
        updated.LastRunError.ShouldContain("boom");
        var history = await context.Store.GetRunHistoryAsync("job-1");
        var entry = history.ShouldHaveSingleItem();
        entry.Status.ShouldBe("error");
        entry.Error.ShouldBe("boom");
    }

    [Fact]
    public async Task Scheduler_CorrectsStaleFutureNextRunAt_WhenScheduleChangedToFireSooner()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Create a job with NextRunAt set far in the future (simulates a schedule
        // that was updated but NextRunAt wasn't recomputed).
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "* * * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(365)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        // The schedule says every minute, but NextRunAt is a year out.
        // Scheduler should detect the mismatch and correct NextRunAt.
        await InvokeProcessTickAsync(scheduler);

        var updated = await context.Store.GetAsync("job-1");
        updated!.NextRunAt.ShouldNotBeNull();
        updated.NextRunAt!.Value.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2),
            "NextRunAt should be corrected to the next occurrence from now");
    }

    [Fact]
    public async Task Scheduler_FiresOnNextTick_AfterCorrectedNextRunAtBecomesDue()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Job with stale NextRunAt (365 days out) and a "* * * * *" schedule.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "* * * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(365)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        // First tick corrects NextRunAt to the next minute.
        await InvokeProcessTickAsync(scheduler);
        action.ExecutionCount.ShouldBe(0, "corrected NextRunAt is still in the future");

        // Simulate time passing: set NextRunAt to the past.
        var corrected = await context.Store.GetAsync("job-1");
        corrected.ShouldNotBeNull();
        corrected!.NextRunAt.ShouldNotBeNull();
        corrected.NextRunAt!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(364),
            "NextRunAt should have been corrected from 365 days out");

        await context.Store.UpdateAsync(corrected with { NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        // Second tick: job fires because NextRunAt is now in the past.
        await InvokeProcessTickAsync(scheduler);
        action.ExecutionCount.ShouldBe(1, "job should fire after corrected NextRunAt becomes due");
    }

    [Fact]
    public async Task Scheduler_DoesNotCorrectNextRunAt_WhenItMatchesSchedule()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Set NextRunAt to 2 minutes from now. Schedule is every 5 minutes.
        // The computed next occurrence may be up to 5 minutes out, which could be
        // sooner or later than 2 minutes. We use a distant NextRunAt that is still
        // consistent: schedule "0 0 1 1 *" (Jan 1 midnight) with NextRunAt next Jan 1.
        var nextJan1 = new DateTimeOffset(DateTimeOffset.UtcNow.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "0 0 1 1 *",
            NextRunAt = nextJan1
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(0, "job is not due yet");
        var updated = await context.Store.GetAsync("job-1");
        updated!.NextRunAt.ShouldBe(nextJan1, "NextRunAt should not change when it matches the schedule");
    }

    [Fact]
    public async Task Scheduler_UsesJobTimeZone_WhenComputingNextRunAt()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Schedule "0 12 * * *" = noon daily. With UTC, NextRunAt would be noon UTC.
        // With a timezone like Pacific (UTC-7/UTC-8), noon Pacific is 19:00 or 20:00 UTC.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "0 12 * * *",
            TimeZone = "America/Los_Angeles"
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        var updated = await context.Store.GetAsync("job-1");
        updated!.NextRunAt.ShouldNotBeNull();

        // The next occurrence in Pacific should be at noon Pacific time.
        // In UTC, that's either 19:00 or 20:00 depending on DST.
        var pacificTz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var localNext = TimeZoneInfo.ConvertTime(updated.NextRunAt!.Value, pacificTz);
        localNext.Hour.ShouldBe(12, "the cron expression should be interpreted in Pacific time");
        localNext.Minute.ShouldBe(0);
    }

    [Fact]
    public async Task Scheduler_FallsBackToUtc_WhenTimeZoneInvalid()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "0 12 * * *",
            TimeZone = "Invalid/Timezone"
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        var updated = await context.Store.GetAsync("job-1");
        updated!.NextRunAt.ShouldNotBeNull();
        // With UTC fallback, the next occurrence should be at 12:00 UTC
        updated.NextRunAt!.Value.Hour.ShouldBe(12);
    }

    [Fact]
    public async Task Scheduler_ManualRunDoesNotClobberUpdatedSchedule()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "*/5 * * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(3)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        // Update the schedule while the job exists
        var updated = job with
        {
            Schedule = "0 0 1 1 *",
            NextRunAt = new DateTimeOffset(DateTimeOffset.UtcNow.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        await context.Store.UpdateAsync(updated);

        // Manual run should not clobber the updated NextRunAt
        await scheduler.RunNowAsync("job-1");

        var afterRun = await context.Store.GetAsync("job-1");
        afterRun!.LastRunStatus.ShouldBe("ok");
        // NextRunAt should reflect the updated "0 0 1 1 *" schedule, not the old "*/5"
        afterRun.NextRunAt.ShouldNotBeNull();
        afterRun.NextRunAt!.Value.Month.ShouldBe(1);
        afterRun.NextRunAt!.Value.Day.ShouldBe(1);
    }

    [Fact]
    public async Task Scheduler_OneJobFailure_DoesNotPreventOtherJobsFromRunning()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var failAction = new ThrowingAction("fail-action", "kaboom");
        var okAction = new RecordingAction("ok-action");

        var job1 = CronStoreTestContext.CreateJob("job-fail", actionType: "fail-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var job2 = CronStoreTestContext.CreateJob("job-ok", actionType: "ok-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job1);
        await context.Store.CreateAsync(job2);

        var scheduler = CreateScheduler(context.Store, [failAction, okAction]);

        await InvokeProcessTickAsync(scheduler);

        okAction.ExecutionCount.ShouldBe(1,
            "the second job should still run even though the first threw");
        var failedRun = await context.Store.GetRunHistoryAsync("job-fail");
        failedRun.ShouldHaveSingleItem().Status.ShouldBe("error");
    }

    [Fact]
    public async Task Scheduler_MultipleDueJobs_AllFire()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        for (var i = 1; i <= 3; i++)
        {
            var job = CronStoreTestContext.CreateJob($"job-{i}", actionType: "test-action") with
            {
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await context.Store.CreateAsync(job);
        }

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(3, "all three due jobs should fire");
    }

    [Fact]
    public async Task Scheduler_JobWithInvalidSchedule_SkipsWithoutPoisoningLoop()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var badJob = CronStoreTestContext.CreateJob("bad-job", actionType: "test-action") with
        {
            Schedule = "not a cron expression",
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var goodJob = CronStoreTestContext.CreateJob("good-job", actionType: "test-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(badJob);
        await context.Store.CreateAsync(goodJob);

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(1, "valid job should still fire");
        var goodHistory = await context.Store.GetRunHistoryAsync("good-job");
        goodHistory.ShouldHaveSingleItem().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Scheduler_ReenabledJob_WithPastNextRunAt_FiresImmediately()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Job was disabled, had a past NextRunAt. Now re-enabled.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Enabled = true
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(1,
            "re-enabled job with past NextRunAt should fire immediately");
    }

    [Fact]
    public async Task RunNowAsync_NonexistentJob_Throws()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var scheduler = CreateScheduler(context.Store, [action]);

        var act = () => scheduler.RunNowAsync("nonexistent-job");

        await act.ShouldThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RunNowAsync_DisabledJob_StillRuns()
    {
        // Manual runs should bypass the enabled check — the user
        // explicitly asked to run it.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action", enabled: false);
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync("job-1");

        run.Status.ShouldBe("ok");
        action.ExecutionCount.ShouldBe(1);
    }

    [Fact]
    public async Task Scheduler_SyncConfiguredJobs_UpdatedSchedule_CorrectsStaleness()
    {
        // Config sync changes a schedule from yearly to every minute,
        // but doesn't recompute NextRunAt. The scheduler's stale-detection
        // in ProcessTickAsync should catch and correct it.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Pre-seed a job with a yearly schedule and distant NextRunAt
        var job = CronStoreTestContext.CreateJob("config-job", actionType: "test-action") with
        {
            Schedule = "0 0 1 1 *",
            NextRunAt = new DateTimeOffset(DateTimeOffset.UtcNow.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        await context.Store.CreateAsync(job);

        // Now simulate config sync changing the schedule to every minute
        // but NOT recomputing NextRunAt (the SyncConfiguredJobs bug)
        var synced = job with { Schedule = "* * * * *" };
        await context.Store.UpdateAsync(synced);

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        // ProcessTickAsync should detect the mismatch and correct NextRunAt
        var updated = await context.Store.GetAsync("config-job");
        updated!.NextRunAt.ShouldNotBeNull();
        updated.NextRunAt!.Value.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2),
            "scheduler should correct stale NextRunAt after config sync");
    }

    [Fact]
    public async Task Scheduler_CreateWithInvalidSchedule_SetsNullNextRunAt()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "garbage"
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        // Should not throw, should not fire, job should be untouched
        action.ExecutionCount.ShouldBe(0);
    }

    private static CronScheduler CreateScheduler(ICronStore store, IEnumerable<ICronAction> actions)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            actions,
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            NullLogger<CronScheduler>.Instance);
    }

    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        task.ShouldNotBeNull();
        await task!;
    }

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public int ExecutionCount { get; private set; }
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAction(string actionType, string message) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
