using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using static BotNexus.Gateway.Tests.Diagnostics.CronDispatchFreshnessProbeTests;
using ManualTimeProvider = BotNexus.Gateway.Tests.Diagnostics.CronDispatchFreshnessProbeTests.ManualTimeProvider;

namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// #4732 round-3 HIGH-1: the probe confirms a stall across two observations, so the watchdog must
/// re-probe on the next check instead of waiting for the next doubling rung, and must not claim
/// "no due cron job is overdue" while a candidate is awaiting confirmation.
/// </summary>
public sealed class WatchdogDispatchConfirmationLatencyTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Watchdog_RealProbe_StalledJob_CriticalWithinGracePlusConfirmationWindow()
    {
        var cron = new CronOptions();
        var watchdog = new LivenessWatchdogOptions();
        var grace = CronDispatchFreshnessProbe.GetGrace(cron);
        var confirm = TimeSpan.FromSeconds(2 * cron.TickIntervalSeconds);
        var bound = watchdog.CriticalThreshold + grace + confirm + watchdog.CheckInterval;

        var time = new ManualTimeProvider(Start);
        var store = Substitute.For<ICronStore>();
        store.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CronJob>>([new CronJob
            {
                Id = JobId.From("stuck"),
                Name = "stuck",
                Schedule = "*/5 * * * *",
                ActionType = "agent-prompt",
                NextRunAt = Start,
            }]));
        store.ListRunningRunsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CronRun>>([]));
        var probe = CreateProbe(store, time, cron);
        var logger = new RecordingLogger<LivenessWatchdogService>();
        var service = new LivenessWatchdogService(
            new ClockActivityTracker(time, Start),
            new ResponsiveProbe(),
            Options.Create(watchdog),
            logger,
            probe);

        TimeSpan? firstSighting = null;
        TimeSpan? criticalAt = null;
        while (time.Now - Start <= bound + TimeSpan.FromHours(2) && criticalAt is null)
        {
            time.Now += watchdog.CheckInterval;
            var before = logger.Entries.Count;
            await service.CheckLivenessAsync(CancellationToken.None);
            var added = logger.Entries.Skip(before).ToList();
            if (firstSighting is null && added.Any(e => e.Message.Contains("awaiting confirmation")))
            {
                firstSighting = time.Now - Start;
                Assert.DoesNotContain(added, e => e.Level == LogLevel.Critical);
                Assert.DoesNotContain(added, e => e.Message.Contains("no due cron job is overdue"));
            }

            if (added.Any(e => e.Level == LogLevel.Critical))
                criticalAt = time.Now - Start;
        }

        Assert.NotNull(firstSighting);
        Assert.NotNull(criticalAt);
        Assert.True(criticalAt > firstSighting, $"CRITICAL on first sighting ({criticalAt})");
        Assert.True(criticalAt <= bound, $"CRITICAL at {criticalAt}, bound {bound}");
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
    }

    private sealed class ClockActivityTracker(ManualTimeProvider time, DateTimeOffset last) : IActivityTracker
    {
        public void RecordActivity() { }
        public TimeSpan TimeSinceLastActivity => time.Now - last;
        public DateTimeOffset LastActivityUtc => last;
    }

    private sealed class ResponsiveProbe : IThreadPoolProbe
    {
        public Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
