using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// #4732 review: the real <see cref="CronDispatchFreshnessProbe"/> must fail CLOSED - an unreadable
/// cron store hides exactly the kind of stall #4689 was about, so it is not evidence of health.
/// </summary>
public sealed class CronDispatchFreshnessProbeTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CronDispatchFreshness_StoreThrows_ReportsNotFresh()
    {
        var store = Substitute.For<ICronStore>();
        store.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CronJob>>>(_ => throw new InvalidOperationException("database is locked"));
        var probe = CreateProbe(store, new ManualTimeProvider(Start));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        Assert.True(result.StoreUnreadable);
    }

    [Fact]
    public async Task CronDispatchFreshness_WatchdogEscalatesWhenProbeThrows()
    {
        var logger = new RecordingLogger<LivenessWatchdogService>();
        var service = new LivenessWatchdogService(
            new StubActivityTracker(TimeSpan.FromMinutes(31)),
            new StubThreadPoolProbe(),
            Options.Create(new LivenessWatchdogOptions()),
            logger,
            new ThrowingDispatchProbe());

        await service.CheckLivenessAsync(CancellationToken.None);

        var critical = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("could not be read", critical.Message, StringComparison.OrdinalIgnoreCase);
        // #4732 L6: a probe bug is named as such, not disguised as an unreadable store.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("freshness probe threw"));
    }

    [Fact]
    public async Task CronDispatchFreshness_ManyOverdueJobs_CriticalListsAtMostTen()
    {
        // #4732 L5: cap the job-id list so a large backlog does not produce an unbounded log line.
        var logger = new RecordingLogger<LivenessWatchdogService>();
        var ids = Enumerable.Range(0, 15).Select(i => $"job-{i:D2}").ToArray();
        var service = new LivenessWatchdogService(
            new StubActivityTracker(TimeSpan.FromMinutes(31)),
            new StubThreadPoolProbe(),
            Options.Create(new LivenessWatchdogOptions()),
            logger,
            new FixedDispatchProbe(new DispatchFreshnessResult(TimeSpan.FromHours(2), ids)));

        await service.CheckLivenessAsync(CancellationToken.None);

        var critical = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("job-09", critical.Message);
        Assert.DoesNotContain("job-10", critical.Message);
        Assert.Contains("+5 more", critical.Message);
    }

    private sealed class FixedDispatchProbe(DispatchFreshnessResult result) : IDispatchFreshnessProbe
    {
        public Task<DispatchFreshnessResult> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private static readonly TimeSpan Grace = CronDispatchFreshnessProbe.GetGrace(new CronOptions());

    [Fact]
    public async Task CronDispatchFreshness_FreshDueJob_IsHealthy()
    {
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("due-now", nextRun: Start)), time);
        time.Now = Start + Grace - TimeSpan.FromMinutes(1);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
        Assert.False(result.StoreUnreadable);
    }

    [Fact]
    public async Task CronDispatchFreshness_OverdueEnabledJobPastGrace_IsStale()
    {
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("stuck", nextRun: Start + TimeSpan.FromMinutes(5))), time);
        time.Now = Start + TimeSpan.FromMinutes(5) + Grace + TimeSpan.FromMinutes(5);

        // #4732 MEDIUM-1: the first stale observation is only a candidate; it must be confirmed.
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);
        time.Now += TimeSpan.FromMinutes(5);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        Assert.False(result.StoreUnreadable);
        Assert.Equal(["stuck"], result.OverdueJobIds);
        Assert.Equal(Grace + TimeSpan.FromMinutes(10), result.OldestOverdue);
    }

    [Fact]
    public async Task CronDispatchFreshness_HostSleepWallClockJump_FirstProbeIsNotStale()
    {
        // #4732 MEDIUM-1: the host sleeps 2h; a job fell due during the gap. The scheduler has not
        // had a tick yet, so a single post-resume observation is not evidence of a stall.
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("slept", nextRun: Start + TimeSpan.FromMinutes(5))), time);
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);

        time.JumpWallClock(TimeSpan.FromHours(2));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_SingleStaleObservation_IsNotStaleUntilConfirmed()
    {
        // #4732 MEDIUM-1: even without a detectable jump (e.g. a sleep the monotonic clock also
        // counted), one post-resume observation is only a candidate.
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("slept", nextRun: Start + TimeSpan.FromMinutes(5))), time);
        time.Now = Start + TimeSpan.FromHours(2);

        var first = await probe.CheckAsync(CancellationToken.None);

        Assert.False(first.IsStalled);
        Assert.Equal(1, first.PendingConfirmation);
    }

    [Fact]
    public async Task CronDispatchFreshness_JumpBetweenCandidateAndConfirmation_ResetsBaseline()
    {
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("stuck", nextRun: Start)), time);
        time.Now = Start + Grace + TimeSpan.FromMinutes(1);
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);

        time.JumpWallClock(TimeSpan.FromHours(2));

        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_HostSleepThenSchedulerCatchesUp_NeverStale()
    {
        var time = new ManualTimeProvider(Start);
        var job = Job("slept", nextRun: Start + TimeSpan.FromMinutes(5));
        var store = Substitute.For<ICronStore>();
        IReadOnlyList<CronJob> jobs = [job];
        store.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(jobs));
        var probe = CreateProbe(store, time);

        time.JumpWallClock(TimeSpan.FromHours(2));
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);

        // Scheduler resumes and dispatches: NextRunAt advances, so the candidate is not confirmed.
        jobs = [job with { NextRunAt = time.Now + TimeSpan.FromMinutes(5) }];
        time.Now += TimeSpan.FromMinutes(30);

        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_PersistentStallAfterJump_EscalatesOnConfirmation()
    {
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("stuck", nextRun: Start + TimeSpan.FromMinutes(5))), time);

        time.JumpWallClock(TimeSpan.FromHours(2));
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);

        // After resume the job is measured from the new baseline: past grace it is a candidate...
        time.Now += Grace + TimeSpan.FromMinutes(1);
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);

        // ...less than two ticks later it is still only a candidate...
        time.Now += TimeSpan.FromSeconds(30);
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);

        // ...and the same (jobId, NextRunAt) still overdue two ticks after first sighting is a stall.
        time.Now += TimeSpan.FromSeconds(90);
        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        Assert.Equal(["stuck"], result.OverdueJobIds);
    }

    [Theory]
    [InlineData(10800)]
    public async Task CronDispatchFreshness_PerJobTimeoutOverride_LongRunIsNotStale(int timeoutSeconds)
    {
        // #4732 MEDIUM-2: a job allowed to run 3h is legitimately overdue for 90m. (Unlimited jobs are
        // covered by the running-run tests below: they are exempt only while a run is in flight.)
        var time = new ManualTimeProvider(Start);
        var job = Job("long-run", nextRun: Start) with
        {
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = timeoutSeconds },
        };
        var probe = CreateProbe(StoreWith(job), time);

        time.Now = Start + TimeSpan.FromMinutes(88);
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);
        time.Now = Start + TimeSpan.FromMinutes(90);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_UnlimitedJobWithRunningRun_IsNotStale()
    {
        // #4732 round-3 MEDIUM-1: an unlimited job legitimately holds NextRunAt in the past while it runs.
        var time = new ManualTimeProvider(Start);
        var job = Unlimited("forever");
        var store = StoreWith(job);
        store.ListRunningRunsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CronRun>>([new CronRun { Id = RunId.From("run-1"), JobId = job.Id, Status = "running" }]));
        var probe = CreateProbe(store, time);

        time.Now = Start + Grace + TimeSpan.FromHours(5);
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);
        time.Now += TimeSpan.FromMinutes(5);

        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_UnlimitedJobWithoutRunningRun_IsStaleAfterDefaultGrace()
    {
        // #4732 round-3 MEDIUM-1: no run in flight means the tick loop never fired it - a dead tick
        // loop whose only jobs are unlimited must still be reported.
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Unlimited("forever")), time);

        time.Now = Start + Grace + TimeSpan.FromMinutes(1);
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);
        time.Now += TimeSpan.FromMinutes(5);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        Assert.Equal(["forever"], result.OverdueJobIds);
    }

    private static CronJob Unlimited(string id) => Job(id, nextRun: Start) with
    {
        Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = 0 },
    };

    [Fact]
    public async Task CronDispatchFreshness_PerJobTimeoutOverride_StillStaleBeyondItsOwnTimeout()
    {
        var time = new ManualTimeProvider(Start);
        var job = Job("long-run", nextRun: Start) with
        {
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = 10800 },
        };
        var probe = CreateProbe(StoreWith(job), time);

        time.Now = Start + TimeSpan.FromHours(3) + TimeSpan.FromMinutes(5);
        Assert.False((await probe.CheckAsync(CancellationToken.None)).IsStalled);
        time.Now += TimeSpan.FromMinutes(5);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        Assert.Equal(["long-run"], result.OverdueJobIds);
    }

    [Fact]
    public async Task CronDispatchFreshness_BackoffFloorDefersDueTime()
    {
        var time = new ManualTimeProvider(Start);
        var job = Job("backing-off", nextRun: Start) with { BackoffUntil = Start + TimeSpan.FromHours(2) };
        var probe = CreateProbe(StoreWith(job), time);
        time.Now = Start + Grace + TimeSpan.FromMinutes(10);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_DisabledExpiredAndInvalidScheduleJobs_AreIgnored()
    {
        var time = new ManualTimeProvider(Start);
        var store = StoreWith(
            Job("disabled", nextRun: Start) with { Enabled = false },
            Job("expired", nextRun: Start) with { ExpiresAt = Start + TimeSpan.FromMinutes(1) },
            Job("invalid", nextRun: Start) with { Schedule = "not a cron" },
            Job("never-scheduled", nextRun: null));
        var probe = CreateProbe(store, time);
        time.Now = Start + Grace + TimeSpan.FromHours(1);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
        Assert.Empty(result.OverdueJobIds);
    }

    [Fact]
    public async Task CronDispatchFreshness_CronDisabled_IsHealthyAndDoesNotReadStore()
    {
        var time = new ManualTimeProvider(Start);
        var store = StoreWith(Job("stuck", nextRun: Start));
        var probe = CreateProbe(store, time, new CronOptions { Enabled = false });
        time.Now = Start + Grace + TimeSpan.FromHours(1);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
        await store.DidNotReceiveWithAnyArgs().ListAsync(default, default);
    }

    [Fact]
    public async Task CronDispatchFreshness_JobDueBeforeStartup_IsMeasuredFromStartup()
    {
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("missed-during-downtime", nextRun: Start - TimeSpan.FromDays(1))), time);
        time.Now = Start + Grace - TimeSpan.FromMinutes(1);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_NoCronStoreRegistered_IsHealthy()
    {
        var probe = new CronDispatchFreshnessProbe(new ServiceCollection().BuildServiceProvider(), new ManualTimeProvider(Start));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_StoreThrows_LogsDistinctUnreadableWarning()
    {
        var store = Substitute.For<ICronStore>();
        store.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CronJob>>>(_ => throw new InvalidOperationException("database is locked"));
        var logger = new RecordingLogger<CronDispatchFreshnessProbe>();
        var probe = CreateProbe(store, new ManualTimeProvider(Start), logger: logger);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("could not be read", entry.Message);
    }

    private static ICronStore StoreWith(params CronJob[] jobs)
    {
        var store = Substitute.For<ICronStore>();
        store.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CronJob>>(jobs));
        store.ListRunningRunsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CronRun>>([]));
        return store;
    }

    private static CronJob Job(string id, DateTimeOffset? nextRun) => new()
    {
        Id = JobId.From(id),
        Name = id,
        Schedule = "*/5 * * * *",
        ActionType = "agent-prompt",
        NextRunAt = nextRun,
    };

    internal static CronDispatchFreshnessProbe CreateProbe(
        ICronStore store,
        TimeProvider time,
        CronOptions? options = null,
        ILogger<CronDispatchFreshnessProbe>? logger = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<CronOptions>>();
        monitor.CurrentValue.Returns(options ?? new CronOptions());
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(monitor)
            .BuildServiceProvider();
        return new CronDispatchFreshnessProbe(services, time, logger);
    }

    internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private TimeSpan _monotonicOffset;

        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;

        // Monotonic clock tracks wall-clock unless JumpWallClock is used.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (Now - DateTimeOffset.UnixEpoch - _monotonicOffset).Ticks;

        /// <summary>Forward wall-clock jump (NTP step / host suspend) the monotonic clock did not see.</summary>
        public void JumpWallClock(TimeSpan delta)
        {
            Now += delta;
            _monotonicOffset += delta;
        }
    }

    private sealed class ThrowingDispatchProbe : IDispatchFreshnessProbe
    {
        public Task<DispatchFreshnessResult> CheckAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("database is locked");
    }

    private sealed class StubActivityTracker(TimeSpan elapsed) : IActivityTracker
    {
        public void RecordActivity() { }
        public TimeSpan TimeSinceLastActivity => elapsed;
        public DateTimeOffset LastActivityUtc => Start - elapsed;
    }

    private sealed class StubThreadPoolProbe : IThreadPoolProbe
    {
        public Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    internal sealed record LogEntry(LogLevel Level, string Message);

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
