using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        tracker.TimeSinceLastActivity.TotalSeconds.ShouldBeLessThan(1);
    }
}

public sealed class LivenessWatchdogServiceTests
{
    [Fact]
    public async Task CheckLivenessAsync_WhenInactiveAboveWarningThreshold_LogsOnce()
    {
        var tracker = new StubActivityTracker(TimeSpan.FromMinutes(20));
        var logger = new RecordingLogger();
        var service = CreateService(tracker, new StubThreadPoolProbe(true), logger);

        await service.CheckLivenessAsync(CancellationToken.None);
        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Entries.Count(entry => entry.Level == LogLevel.Warning).ShouldBe(1);
        logger.Entries.ShouldContain(entry => entry.Message.Contains("no activity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckLivenessAsync_WhenCriticalAndSchedulerResponsive_LogsWarningAndNoFatal()
    {
        var tracker = new StubActivityTracker(TimeSpan.FromMinutes(31));
        var probe = new StubThreadPoolProbe(true);
        var logger = new RecordingLogger();
        var service = CreateService(tracker, probe, logger);

        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("scheduler probe succeeded", StringComparison.OrdinalIgnoreCase));
        logger.Entries.ShouldNotContain(entry => entry.Level == LogLevel.Critical);
        probe.Timeouts.ShouldBe([TimeSpan.FromSeconds(5)]);
    }

    [Fact]
    public async Task CheckLivenessAsync_WhenProbeTimesOut_LogsExactlyOneFatalPerEpisode()
    {
        var tracker = new StubActivityTracker(TimeSpan.FromMinutes(31));
        var probe = new StubThreadPoolProbe(false);
        var logger = new RecordingLogger();
        var service = CreateService(tracker, probe, logger);

        await service.CheckLivenessAsync(CancellationToken.None);
        await service.CheckLivenessAsync(CancellationToken.None);
        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Entries.Count(entry => entry.Level == LogLevel.Critical).ShouldBe(1);
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Critical &&
            entry.Message.Contains("scheduler probe timed out", StringComparison.OrdinalIgnoreCase));
        probe.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task CheckLivenessAsync_AfterRecovery_EvaluatesNewCriticalEpisode()
    {
        var tracker = new StubActivityTracker(TimeSpan.FromMinutes(31));
        var probe = new StubThreadPoolProbe(false);
        var logger = new RecordingLogger();
        var service = CreateService(tracker, probe, logger);

        await service.CheckLivenessAsync(CancellationToken.None);
        tracker.Elapsed = TimeSpan.Zero;
        await service.CheckLivenessAsync(CancellationToken.None);
        tracker.Elapsed = TimeSpan.FromMinutes(31);
        await service.CheckLivenessAsync(CancellationToken.None);

        logger.Entries.Count(entry => entry.Level == LogLevel.Critical).ShouldBe(2);
        logger.Entries.Count(entry => entry.Level == LogLevel.Information && entry.Message.Contains("recovered", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
        probe.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task CheckLivenessAsync_WhenProbeIsCancelled_LogsNoFatal()
    {
        var tracker = new StubActivityTracker(TimeSpan.FromMinutes(31));
        var probe = new CancellingThreadPoolProbe();
        var logger = new RecordingLogger();
        var service = CreateService(tracker, probe, logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => service.CheckLivenessAsync(cancellation.Token));

        logger.Entries.ShouldNotContain(entry => entry.Level == LogLevel.Critical);
    }

    [Fact]
    public void DefaultOptions_CriticalProbeTimeout_Is5Seconds()
    {
        new LivenessWatchdogOptions().CriticalProbeTimeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AddBotNexusGateway_RegistersProbeAndBindsLivenessOptions()
    {
        var values = new Dictionary<string, string?>
        {
            ["gateway:livenessWatchdog:criticalProbeTimeout"] = "00:00:02"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();

        services.AddBotNexusGateway(configuration);

        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(IThreadPoolProbe));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<LivenessWatchdogOptions>>().Value.CriticalProbeTimeout
            .ShouldBe(TimeSpan.FromSeconds(2));
    }

    private static LivenessWatchdogService CreateService(
        IActivityTracker tracker,
        IThreadPoolProbe probe,
        RecordingLogger logger)
    {
        return new LivenessWatchdogService(
            tracker,
            probe,
            Options.Create(new LivenessWatchdogOptions()),
            logger);
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
        public int CallCount { get; private set; }
        public List<TimeSpan> Timeouts { get; } = [];

        public Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Timeouts.Add(timeout);
            return Task.FromResult(result);
        }
    }

    private sealed class CancellingThreadPoolProbe : IThreadPoolProbe
    {
        public Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromCanceled<bool>(cancellationToken);
    }

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

    private sealed record LogEntry(LogLevel Level, string Message);
}
