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
    /// Duration of inactivity after which a critical alert is logged. Default: 30 minutes.
    /// Previously 10 minutes, which caused excessive false positives during normal quiet periods
    /// (overnight, between cron jobs). Increased to 30 minutes per #1320.
    /// </summary>
    public TimeSpan CriticalThreshold { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Background service that monitors gateway activity and logs warnings when the process
/// appears unresponsive. Detects the scenario where the gateway is alive (port open)
/// but unable to schedule work (deadlock/threadpool exhaustion).
/// </summary>
public sealed class LivenessWatchdogService(
    IActivityTracker activityTracker,
    IOptions<LivenessWatchdogOptions> options,
    ILogger<LivenessWatchdogService> logger) : BackgroundService
{
    private readonly IActivityTracker _activityTracker = activityTracker;
    private readonly LivenessWatchdogOptions _options = options.Value;
    private readonly ILogger<LivenessWatchdogService> _logger = logger;

    private bool _warningEmitted;
    private bool _criticalEmitted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the gateway time to start up before monitoring
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckLiveness();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Liveness watchdog check failed unexpectedly.");
            }

            await Task.Delay(_options.CheckInterval, stoppingToken);
        }
    }

    internal void CheckLiveness()
    {
        var elapsed = _activityTracker.TimeSinceLastActivity;

        if (elapsed >= _options.CriticalThreshold)
        {
            if (!_criticalEmitted)
            {
                _logger.LogCritical(
                    "Gateway liveness CRITICAL: no activity for {Elapsed}. Last activity at {LastActivity}. " +
                    "Possible deadlock or threadpool exhaustion.",
                    elapsed,
                    _activityTracker.LastActivityUtc);
                _criticalEmitted = true;
            }
        }
        else if (elapsed >= _options.WarningThreshold)
        {
            if (!_warningEmitted)
            {
                _logger.LogWarning(
                    "Gateway liveness WARNING: no activity for {Elapsed}. Last activity at {LastActivity}.",
                    elapsed,
                    _activityTracker.LastActivityUtc);
                _warningEmitted = true;
            }
        }
        else
        {
            // Activity resumed — reset flags
            if (_warningEmitted || _criticalEmitted)
            {
                _logger.LogInformation(
                    "Gateway liveness recovered. Activity resumed after {Elapsed} of inactivity.",
                    elapsed);
            }
            _warningEmitted = false;
            _criticalEmitted = false;
        }
    }
}
