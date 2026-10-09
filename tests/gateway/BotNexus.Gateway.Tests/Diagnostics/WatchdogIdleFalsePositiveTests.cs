using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// #4689 review round 1 (HIGH): a gateway with nothing due is legitimately idle. Long inactivity
/// alone must never be reported as a CRITICAL dispatch stall.
/// </summary>
public sealed class WatchdogIdleFalsePositiveTests
{
    [Fact]
    public async Task LongIdle_WithNoOverdueJobs_DoesNotEscalateToCritical()
    {
        var logger = new RecordingLogger();
        var service = new LivenessWatchdogService(
            new FixedTracker(TimeSpan.FromHours(15)),
            new AlwaysResponsiveProbe(),
            Options.Create(new LivenessWatchdogOptions()),
            logger);

        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Levels.ShouldNotContain(LogLevel.Critical);
    }

    private sealed class FixedTracker(TimeSpan elapsed) : IActivityTracker
    {
        public void RecordActivity() { }
        public TimeSpan TimeSinceLastActivity => elapsed;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.UtcNow - elapsed;
    }

    private sealed class AlwaysResponsiveProbe : IThreadPoolProbe
    {
        public Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class RecordingLogger : ILogger<LivenessWatchdogService>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
