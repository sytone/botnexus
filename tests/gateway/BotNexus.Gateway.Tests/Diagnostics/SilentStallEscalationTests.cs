using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// Covers the detection failure filed upstream as sytone/botnexus#4689: the watchdog probed whether
/// the THREAD POOL responds, concluded "Gateway is responsive and idle", and then latched - so a
/// scheduler that was dispatching zero jobs stayed silent for 15h19m on a live host.
/// </summary>
/// <remarks>
/// A passing thread-pool probe proves the process can schedule a callback. It does NOT prove the
/// scheduler is dispatching work. These tests assert the two behaviours that close the gap: the
/// episode must RE-ALARM as the gap widens, and a gap far beyond the critical threshold must
/// ESCALATE to Critical rather than being reported as healthy idling.
/// </remarks>
public sealed class SilentStallEscalationTests
{
    /// <summary>
    /// The 15-hour case. Inactivity at many multiples of the critical threshold, with the probe
    /// still passing, is a stalled dispatcher - not an idle gateway.
    /// </summary>
    [Fact]
    public async Task SustainedInactivity_WithPassingProbe_EscalatesToCritical()
    {
        // The real outage: 15h19m of zero dispatch while the probe kept succeeding.
        var tracker = new StubActivityTracker(TimeSpan.FromHours(15) + TimeSpan.FromMinutes(19));
        var logger = new RecordingLogger();
        var service = CreateService(tracker, new StubThreadPoolProbe(true), logger, new StalledDispatchProbe());

        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Entries.ShouldContain(
            entry => entry.Level == LogLevel.Critical,
            "a responsive probe must not downgrade a 15-hour dispatch outage to a warning");
    }

    /// <summary>
    /// The message must not assert a healthy conclusion the probe cannot support.
    /// </summary>
    [Fact]
    public async Task SuppressedWarning_DoesNotClaimTheGatewayIsIdle()
    {
        var tracker = new StubActivityTracker(TimeSpan.FromMinutes(31));
        var logger = new RecordingLogger();
        var service = CreateService(tracker, new StubThreadPoolProbe(true), logger);

        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Entries.ShouldNotContain(
            entry => entry.Message.Contains("responsive and idle", StringComparison.OrdinalIgnoreCase),
            "the probe cannot prove the gateway is merely idle, so it must not say so");
    }

    /// <summary>
    /// De-latching: a widening gap must produce a further alarm. Previously the episode was
    /// evaluated once and every later check returned early, so 30 minutes and 15 hours looked
    /// identical in the log.
    /// </summary>
    [Fact]
    public async Task WideningGap_ReAlarms_RatherThanLatchingSilent()
    {
        var tracker = new StubActivityTracker(TimeSpan.FromMinutes(31));
        var logger = new RecordingLogger();
        var service = CreateService(tracker, new StubThreadPoolProbe(true), logger);

        await service.CheckLivenessAsync(CancellationToken.None);
        var afterFirst = logger.Entries.Count;

        // The outage continues and the gap more than doubles.
        tracker.Elapsed = TimeSpan.FromHours(4);
        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Entries.Count.ShouldBeGreaterThan(
            afterFirst,
            "a sustained, widening outage must re-alarm instead of latching after one line");
    }

    /// <summary>
    /// Non-vacuity guard: the watchdog must stay quiet when the gateway is genuinely healthy,
    /// proving the escalation is driven by the gap and not unconditional.
    /// </summary>
    [Fact]
    public async Task HealthyGateway_StaysQuiet()
    {
        var tracker = new StubActivityTracker(TimeSpan.FromMinutes(1));
        var logger = new RecordingLogger();
        var service = CreateService(tracker, new StubThreadPoolProbe(true), logger);

        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Entries.ShouldNotContain(entry => entry.Level == LogLevel.Critical);
        logger.Entries.ShouldNotContain(entry => entry.Level == LogLevel.Warning);
    }

    private static LivenessWatchdogService CreateService(
        IActivityTracker tracker,
        IThreadPoolProbe probe,
        RecordingLogger logger,
        IDispatchFreshnessProbe? dispatchProbe = null)
        => new(tracker, probe, Options.Create(new LivenessWatchdogOptions()), logger, dispatchProbe);

    private sealed class StalledDispatchProbe : IDispatchFreshnessProbe
    {
        public Task<DispatchFreshnessResult> CheckAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DispatchFreshnessResult(TimeSpan.FromHours(14), ["32b046eac183"]));
    }

    private sealed class StubActivityTracker(TimeSpan elapsed) : IActivityTracker
    {
        public TimeSpan Elapsed { get; set; } = elapsed;
        public void RecordActivity() => Elapsed = TimeSpan.Zero;
        public TimeSpan TimeSinceLastActivity => Elapsed;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.UtcNow - Elapsed;
    }

    private sealed class StubThreadPoolProbe(bool result) : IThreadPoolProbe
    {
        public Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger<LivenessWatchdogService>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
