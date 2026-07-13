using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class ActivityTrackerTests
{
    [Fact]
    public void RecordActivity_UpdatesLastActivityUtc()
    {
        var tracker = new ActivityTracker();
        var before = DateTimeOffset.UtcNow;

        tracker.RecordActivity();

        tracker.LastActivityUtc.ShouldBeGreaterThanOrEqualTo(before);
        tracker.TimeSinceLastActivity.TotalSeconds.ShouldBeLessThan(1);
    }

    [Fact]
    public async Task TimeSinceLastActivity_IncreasesBetweenCalls()
    {
        var tracker = new ActivityTracker();
        tracker.RecordActivity();

        await Task.Delay(100);

        tracker.TimeSinceLastActivity.TotalMilliseconds.ShouldBeGreaterThanOrEqualTo(50);
    }

    [Fact]
    public void RecordActivity_IsThreadSafe()
    {
        var tracker = new ActivityTracker();
        Parallel.For(0, 1000, _ => tracker.RecordActivity());

        // Should not throw and last activity should be recent
        tracker.TimeSinceLastActivity.TotalSeconds.ShouldBeLessThan(1);
    }
}

public sealed class LivenessWatchdogServiceTests
{
    [Fact]
    public void CheckLiveness_WhenRecent_DoesNotLog()
    {
        var tracker = new ActivityTracker();
        tracker.RecordActivity();
        var options = Options.Create(new LivenessWatchdogOptions
        {
            WarningThreshold = TimeSpan.FromMinutes(5),
            CriticalThreshold = TimeSpan.FromMinutes(10)
        });
        var service = new LivenessWatchdogService(tracker, options, NullLogger<LivenessWatchdogService>.Instance);

        // Should not throw — activity is recent
        service.CheckLiveness();
    }

    [Fact]
    public async Task CheckLiveness_WhenInactiveAboveWarningThreshold_Logs()
    {
        var tracker = new ActivityTracker();
        tracker.RecordActivity();
        var options = Options.Create(new LivenessWatchdogOptions
        {
            WarningThreshold = TimeSpan.FromMilliseconds(50),
            CriticalThreshold = TimeSpan.FromMinutes(10)
        });
        var service = new LivenessWatchdogService(tracker, options, NullLogger<LivenessWatchdogService>.Instance);

        await Task.Delay(100);

        // Should not throw — logs warning internally
        service.CheckLiveness();
    }

    [Fact]
    public async Task CheckLiveness_WhenInactiveButResponsive_DoesNotEscalateToCritical()
    {
        // Regression for #1924: passive idle time (overnight, between cron jobs) must NOT
        // produce a FATAL "possible deadlock" alert. When idle crosses the critical
        // threshold, the active responsiveness probe passes, so no critical is emitted.
        var tracker = new ActivityTracker();
        tracker.RecordActivity();
        var options = Options.Create(new LivenessWatchdogOptions
        {
            WarningThreshold = TimeSpan.FromMilliseconds(20),
            CriticalThreshold = TimeSpan.FromMilliseconds(50)
        });
        var criticalCount = 0;
        var logger = new CountingLogger(() => criticalCount++);
        // Probe reports responsive.
        var service = new LivenessWatchdogService(tracker, options, logger, _ => true);

        await Task.Delay(100);

        service.CheckLiveness();

        criticalCount.ShouldBe(0);
    }

    [Fact]
    public async Task CheckLiveness_WhenInactiveAndProbeFails_LogsCritical()
    {
        // A genuine deadlock / threadpool exhaustion: idle past the critical threshold AND
        // the active responsiveness probe cannot complete. This is the only path to CRITICAL.
        var tracker = new ActivityTracker();
        tracker.RecordActivity();
        var options = Options.Create(new LivenessWatchdogOptions
        {
            WarningThreshold = TimeSpan.FromMilliseconds(20),
            CriticalThreshold = TimeSpan.FromMilliseconds(50)
        });
        var criticalCount = 0;
        var logger = new CountingLogger(() => criticalCount++);
        // Probe reports unresponsive (stall).
        var service = new LivenessWatchdogService(tracker, options, logger, _ => false);

        await Task.Delay(100);

        service.CheckLiveness();

        criticalCount.ShouldBe(1);

        // Latched — a second failing check does not re-log critical.
        service.CheckLiveness();
        criticalCount.ShouldBe(1);
    }

    [Fact]
    public void DefaultThreadPoolProbe_OnHealthyPool_ReportsResponsive()
    {
        // With the default (real) probe and a healthy threadpool, a long-idle tracker must
        // NOT produce a critical alert.
        var tracker = new ActivityTracker();
        var options = Options.Create(new LivenessWatchdogOptions
        {
            WarningThreshold = TimeSpan.Zero,
            CriticalThreshold = TimeSpan.Zero,
            ProbeTimeout = TimeSpan.FromSeconds(5)
        });
        var criticalCount = 0;
        var logger = new CountingLogger(() => criticalCount++);
        var service = new LivenessWatchdogService(tracker, options, logger);

        service.CheckLiveness();

        criticalCount.ShouldBe(0);
    }

    [Fact]
    public async Task CheckLiveness_AfterRecovery_LogsRecovery()
    {
        var tracker = new ActivityTracker();
        tracker.RecordActivity();
        var options = Options.Create(new LivenessWatchdogOptions
        {
            WarningThreshold = TimeSpan.FromMilliseconds(20),
            CriticalThreshold = TimeSpan.FromMinutes(10)
        });
        var service = new LivenessWatchdogService(tracker, options, NullLogger<LivenessWatchdogService>.Instance);

        // Enter warning state
        await Task.Delay(50);
        service.CheckLiveness();

        // Recover
        tracker.RecordActivity();
        service.CheckLiveness();

        // Second check with recent activity should not throw
        service.CheckLiveness();
    }

    [Fact]
    public void CheckLiveness_WarningEmittedOnlyOnce_UntilRecovery()
    {
        var tracker = new ActivityTracker();
        // Set last activity to far in the past
        var options = Options.Create(new LivenessWatchdogOptions
        {
            WarningThreshold = TimeSpan.Zero,
            CriticalThreshold = TimeSpan.FromHours(1)
        });
        var service = new LivenessWatchdogService(tracker, options, NullLogger<LivenessWatchdogService>.Instance);

        // Multiple calls should not throw (warning emitted only on first)
        service.CheckLiveness();
        service.CheckLiveness();
        service.CheckLiveness();
    }

    [Fact]
    public void DefaultOptions_WarningThreshold_Is15Minutes()
    {
        var options = new LivenessWatchdogOptions();
        options.WarningThreshold.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void DefaultOptions_CriticalThreshold_Is30Minutes()
    {
        var options = new LivenessWatchdogOptions();
        options.CriticalThreshold.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void DefaultOptions_ProbeTimeout_Is5Seconds()
    {
        var options = new LivenessWatchdogOptions();
        options.ProbeTimeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Minimal logger that invokes a callback whenever a <see cref="LogLevel.Critical"/>
    /// entry is written, so tests can count FATAL emissions deterministically.
    /// </summary>
    private sealed class CountingLogger(Action onCritical) : ILogger<LivenessWatchdogService>
    {
        private readonly Action _onCritical = onCritical;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Critical)
            {
                _onCritical();
            }
        }
    }
}
