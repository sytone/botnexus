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
    /// Duration of inactivity after which the watchdog runs an active responsiveness
    /// probe. Default: 30 minutes.
    /// Passive idle time alone NEVER escalates to CRITICAL — see remarks on
    /// <see cref="LivenessWatchdogService"/>. A CRITICAL alert is only emitted when the
    /// active probe cannot schedule work within <see cref="ProbeTimeout"/>, which is a
    /// genuine deadlock / threadpool-exhaustion signal (#1320, #1924).
    /// </summary>
    public TimeSpan CriticalThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the active responsiveness probe is allowed to take before the gateway is
    /// declared unresponsive. Default: 5 seconds. A healthy threadpool schedules and runs
    /// a no-op continuation in well under a millisecond; only a true stall (deadlock or
    /// threadpool exhaustion) blows past this budget.
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Background service that monitors gateway activity and logs warnings when the process
/// appears unresponsive. Detects the scenario where the gateway is alive (port open)
/// but unable to schedule work (deadlock/threadpool exhaustion).
///
/// "Activity" is recorded by <see cref="IActivityTracker"/> from the agent execution
/// choke point (<c>InProcessAgentHandle</c>) on every streamed agent event and on the
/// blocking prompt path, plus at inbound dispatch entry. This means a long-running turn
/// or a cron/soul run keeps the tracker fresh.
///
/// <para>
/// Passive inactivity is <b>expected</b> — the gateway is legitimately idle overnight and
/// between cron jobs. Prior versions treated crossing the critical inactivity threshold as
/// a FATAL "possible deadlock" alert, which produced nightly false positives (#1320, #1924).
/// Crossing the critical threshold now only <i>triggers an active responsiveness probe</i>:
/// the watchdog schedules a no-op onto the threadpool and waits up to
/// <see cref="LivenessWatchdogOptions.ProbeTimeout"/>. If the probe completes, the process is
/// responsive and merely idle (logged at debug, no alert). Only a probe that fails to complete
/// in time — a real deadlock or threadpool exhaustion — escalates to CRITICAL.
/// </para>
/// </summary>
public sealed class LivenessWatchdogService : BackgroundService
{
    private readonly IActivityTracker _activityTracker;
    private readonly LivenessWatchdogOptions _options;
    private readonly ILogger<LivenessWatchdogService> _logger;

    /// <summary>
    /// Active responsiveness probe. Returns <c>true</c> if the threadpool scheduled and ran
    /// work within the timeout, <c>false</c> if it stalled. Injectable for deterministic tests.
    /// </summary>
    private readonly Func<TimeSpan, bool> _responsivenessProbe;

    private bool _warningEmitted;
    private bool _criticalEmitted;

    public LivenessWatchdogService(
        IActivityTracker activityTracker,
        IOptions<LivenessWatchdogOptions> options,
        ILogger<LivenessWatchdogService> logger)
        : this(activityTracker, options, logger, responsivenessProbe: null)
    {
    }

    /// <summary>
    /// Test constructor allowing the responsiveness probe to be overridden.
    /// </summary>
    internal LivenessWatchdogService(
        IActivityTracker activityTracker,
        IOptions<LivenessWatchdogOptions> options,
        ILogger<LivenessWatchdogService> logger,
        Func<TimeSpan, bool>? responsivenessProbe)
    {
        _activityTracker = activityTracker;
        _options = options.Value;
        _logger = logger;
        _responsivenessProbe = responsivenessProbe ?? DefaultThreadPoolProbe;
    }

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
            // Passive idle time is NOT sufficient to declare a deadlock — the gateway is
            // routinely idle overnight and between cron jobs. Actively probe the threadpool:
            // only a probe that cannot complete indicates a genuine stall (#1320, #1924).
            var responsive = _responsivenessProbe(_options.ProbeTimeout);

            if (responsive)
            {
                // Alive and responsive, just idle. Not an alert condition. Clear any prior
                // warning/critical latch so recovery is reported correctly if it fires later.
                if (_criticalEmitted)
                {
                    _logger.LogInformation(
                        "Gateway liveness recovered: responsiveness probe succeeded after {Elapsed} of inactivity.",
                        elapsed);
                }
                else
                {
                    _logger.LogDebug(
                        "Gateway idle for {Elapsed} but responsive (probe passed). No action needed.",
                        elapsed);
                }

                _criticalEmitted = false;
                _warningEmitted = false;
                return;
            }

            if (!_criticalEmitted)
            {
                _logger.LogCritical(
                    "Gateway liveness CRITICAL: responsiveness probe failed to complete within {ProbeTimeout} " +
                    "after {Elapsed} of inactivity. Last activity at {LastActivity}. " +
                    "Deadlock or threadpool exhaustion.",
                    _options.ProbeTimeout,
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

    /// <summary>
    /// Default probe: schedules a no-op onto the threadpool and waits up to <paramref name="timeout"/>.
    /// Returns <c>true</c> if the work ran (responsive), <c>false</c> if it could not be scheduled
    /// in time (deadlock / threadpool exhaustion).
    /// </summary>
    private static bool DefaultThreadPoolProbe(TimeSpan timeout)
    {
        using var scheduled = new ManualResetEventSlim(false);
        if (!ThreadPool.QueueUserWorkItem(static state => ((ManualResetEventSlim)state!).Set(), scheduled))
        {
            // Could not even queue the work item.
            return false;
        }

        return scheduled.Wait(timeout);
    }
}
