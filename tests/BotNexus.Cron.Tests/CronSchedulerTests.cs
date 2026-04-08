using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using FluentAssertions;
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

        action.ExecutionCount.Should().Be(1);
        var history = await context.Store.GetRunHistoryAsync("job-1");
        history.Should().ContainSingle(run => run.Status == "ok");
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

        action.ExecutionCount.Should().Be(0);
        (await context.Store.GetRunHistoryAsync("job-1")).Should().BeEmpty();
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

        run.Status.Should().Be("ok");
        var updated = await context.Store.GetAsync("job-1");
        updated!.LastRunStatus.Should().Be("ok");
        updated.LastRunError.Should().BeNull();
        var history = await context.Store.GetRunHistoryAsync("job-1");
        history.Should().ContainSingle(entry => entry.Status == "ok");
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

        run.Status.Should().Be("error");
        run.Error.Should().Be("boom");
        var updated = await context.Store.GetAsync("job-1");
        updated!.LastRunStatus.Should().Be("error");
        updated.LastRunError.Should().Contain("boom");
        var history = await context.Store.GetRunHistoryAsync("job-1");
        history.Should().ContainSingle(entry => entry.Status == "error" && entry.Error == "boom");
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
        updated!.NextRunAt.Should().NotBeNull();
        updated.NextRunAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2),
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
        action.ExecutionCount.Should().Be(0, "corrected NextRunAt is still in the future");

        // Simulate time passing: set NextRunAt to the past.
        var corrected = await context.Store.GetAsync("job-1");
        corrected.Should().NotBeNull();
        corrected!.NextRunAt.Should().NotBeNull();
        corrected.NextRunAt!.Value.Should().BeBefore(DateTimeOffset.UtcNow.AddDays(364),
            "NextRunAt should have been corrected from 365 days out");

        await context.Store.UpdateAsync(corrected with { NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        // Second tick: job fires because NextRunAt is now in the past.
        await InvokeProcessTickAsync(scheduler);
        action.ExecutionCount.Should().Be(1, "job should fire after corrected NextRunAt becomes due");
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

        action.ExecutionCount.Should().Be(0, "job is not due yet");
        var updated = await context.Store.GetAsync("job-1");
        updated!.NextRunAt.Should().Be(nextJan1, "NextRunAt should not change when it matches the schedule");
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
        updated!.NextRunAt.Should().NotBeNull();

        // The next occurrence in Pacific should be at noon Pacific time.
        // In UTC, that's either 19:00 or 20:00 depending on DST.
        var pacificTz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var localNext = TimeZoneInfo.ConvertTime(updated.NextRunAt!.Value, pacificTz);
        localNext.Hour.Should().Be(12, "the cron expression should be interpreted in Pacific time");
        localNext.Minute.Should().Be(0);
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
        updated!.NextRunAt.Should().NotBeNull();
        // With UTC fallback, the next occurrence should be at 12:00 UTC
        updated.NextRunAt!.Value.Hour.Should().Be(12);
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
        afterRun!.LastRunStatus.Should().Be("ok");
        // NextRunAt should reflect the updated "0 0 1 1 *" schedule, not the old "*/5"
        afterRun.NextRunAt.Should().NotBeNull();
        afterRun.NextRunAt!.Value.Month.Should().Be(1);
        afterRun.NextRunAt!.Value.Day.Should().Be(1);
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
        method.Should().NotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        task.Should().NotBeNull();
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
