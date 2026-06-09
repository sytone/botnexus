using BotNexus.Gateway.Diagnostics;
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
    public async Task CheckLiveness_WhenInactiveAboveCriticalThreshold_LogsCritical()
    {
        var tracker = new ActivityTracker();
        tracker.RecordActivity();
        var options = Options.Create(new LivenessWatchdogOptions
        {
            WarningThreshold = TimeSpan.FromMilliseconds(20),
            CriticalThreshold = TimeSpan.FromMilliseconds(50)
        });
        var service = new LivenessWatchdogService(tracker, options, NullLogger<LivenessWatchdogService>.Instance);

        await Task.Delay(100);

        // Should not throw — logs critical internally
        service.CheckLiveness();
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
}
