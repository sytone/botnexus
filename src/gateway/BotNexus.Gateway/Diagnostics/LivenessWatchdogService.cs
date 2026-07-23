using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Configuration for the liveness watchdog service.
/// </summary>
public sealed class LivenessWatchdogOptions
{
    /// <summary>
    /// How often the watchdog checks for inactivity. Default: 30 seconds.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Duration of inactivity after which a warning is logged. Default: 15 minutes.
    /// </summary>
    public TimeSpan WarningThreshold { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Duration of inactivity after which scheduler responsiveness is verified. Default: 30 minutes.
    /// </summary>
    public TimeSpan CriticalThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the scheduler probe may take before the gateway is declared unresponsive.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan CriticalProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Verifies that work queued to the runtime scheduler can execute within a bounded interval.
/// </summary>
public interface IThreadPoolProbe
{
    /// <summary>
    /// Queues work and reports whether it ran before <paramref name="timeout"/> elapsed.
    /// Cancellation represents host shutdown and must not be interpreted as scheduler failure.
    /// </summary>
    Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Probes the runtime scheduler by queuing a no-op to the managed thread pool.
/// </summary>
public sealed class ThreadPoolProbe : IThreadPoolProbe
{
    /// <inheritdoc />
    public async Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ThreadPool.QueueUserWorkItem(static state => ((TaskCompletionSource)state!).TrySetResult(), completion))
        {
            return false;
        }

        try
        {
            await completion.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// Monitors gateway inactivity and verifies scheduler responsiveness before emitting a fatal alert.
/// Quiet gateways remain actionable without conflating ordinary idle time with scheduler failure.
/// </summary>
public sealed class LivenessWatchdogService : BackgroundService
{
    private readonly IActivityTracker _activityTracker;
    private readonly IThreadPoolProbe _threadPoolProbe;
    private readonly LivenessWatchdogOptions _options;
    private readonly ILogger<LivenessWatchdogService> _logger;
    private bool _warningEmitted;
    private bool _criticalEpisodeEvaluated;

    /// <summary>
    /// Creates the watchdog with the scheduler probe used to corroborate critical inactivity.
    /// </summary>
    public LivenessWatchdogService(
        IActivityTracker activityTracker,
        IThreadPoolProbe threadPoolProbe,
        IOptions<LivenessWatchdogOptions> options,
        ILogger<LivenessWatchdogService> logger)
    {
        _activityTracker = activityTracker;
        _threadPoolProbe = threadPoolProbe;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckLivenessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Liveness watchdog check failed unexpectedly.");
            }

            await Task.Delay(_options.CheckInterval, stoppingToken);
        }
    }

    internal async Task CheckLivenessAsync(CancellationToken cancellationToken)
    {
        var elapsed = _activityTracker.TimeSinceLastActivity;

        if (elapsed >= _options.CriticalThreshold)
        {
            if (_criticalEpisodeEvaluated)
            {
                return;
            }

            var responsive = await _threadPoolProbe.IsResponsiveAsync(
                _options.CriticalProbeTimeout,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _criticalEpisodeEvaluated = true;
            _warningEmitted = true;

            if (responsive)
            {
                _logger.LogWarning(
                    "Gateway liveness WARNING: no activity for {Elapsed}, but scheduler probe succeeded " +
                    "within {CriticalProbeTimeout}. Gateway is responsive and idle. Last activity at {LastActivity}.",
                    elapsed,
                    _options.CriticalProbeTimeout,
                    _activityTracker.LastActivityUtc);
                return;
            }

            _logger.LogCritical(
                "Gateway liveness CRITICAL: scheduler probe timed out after {CriticalProbeTimeout} " +
                "with {Elapsed} of inactivity. Last activity at {LastActivity}. " +
                "Deadlock or thread pool exhaustion may have prevented scheduling.",
                _options.CriticalProbeTimeout,
                elapsed,
                _activityTracker.LastActivityUtc);
            return;
        }

        if (elapsed >= _options.WarningThreshold)
        {
            if (!_warningEmitted)
            {
                _logger.LogWarning(
                    "Gateway liveness WARNING: no activity for {Elapsed}. Last activity at {LastActivity}.",
                    elapsed,
                    _activityTracker.LastActivityUtc);
                _warningEmitted = true;
            }

            return;
        }

        if (_warningEmitted || _criticalEpisodeEvaluated)
        {
            _logger.LogInformation(
                "Gateway liveness recovered. Activity resumed after {Elapsed} of inactivity.",
                elapsed);
        }

        _warningEmitted = false;
        _criticalEpisodeEvaluated = false;
    }
}
