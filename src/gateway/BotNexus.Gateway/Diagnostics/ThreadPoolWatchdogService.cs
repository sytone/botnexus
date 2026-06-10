using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Configuration for the threadpool watchdog service.
/// </summary>
public sealed class ThreadPoolWatchdogOptions
{
    /// <summary>
    /// How often to check threadpool health. Default: 30 seconds.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pending work item count above which a warning is emitted. Default: 100.
    /// A sustained queue depth above this threshold indicates threadpool starvation
    /// or deadlock conditions.
    /// </summary>
    public int QueueDepthThreshold { get; set; } = 100;
}

/// <summary>
/// Abstraction for querying threadpool metrics to support testability.
/// </summary>
public interface IThreadPoolMetrics
{
    /// <summary>Gets the number of pending work items in the threadpool.</summary>
    long PendingWorkItemCount { get; }

    /// <summary>Gets available worker and IO threads.</summary>
    (int WorkerAvailable, int WorkerMax, int WorkerMin, int IoAvailable, int IoMax, int IoMin) GetThreadCounts();
}

/// <summary>
/// Default implementation that reads from the real .NET ThreadPool.
/// </summary>
public sealed class SystemThreadPoolMetrics : IThreadPoolMetrics
{
    public long PendingWorkItemCount => ThreadPool.PendingWorkItemCount;

    public (int WorkerAvailable, int WorkerMax, int WorkerMin, int IoAvailable, int IoMax, int IoMin) GetThreadCounts()
    {
        ThreadPool.GetAvailableThreads(out var workerAvailable, out var ioAvailable);
        ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);
        ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
        return (workerAvailable, workerMax, workerMin, ioAvailable, ioMax, ioMin);
    }
}

/// <summary>
/// Background service that monitors the .NET ThreadPool queue depth and logs diagnostic
/// snapshots when pending work exceeds the configured threshold. Helps detect threadpool
/// exhaustion scenarios where the gateway is alive but unable to schedule new work.
/// </summary>
public sealed class ThreadPoolWatchdogService : BackgroundService
{
    private readonly ThreadPoolWatchdogOptions _options;
    private readonly ILogger<ThreadPoolWatchdogService> _logger;
    private readonly IThreadPoolMetrics _metrics;
    private bool _warningEmitted;

    public ThreadPoolWatchdogService(
        IOptions<ThreadPoolWatchdogOptions> options,
        ILogger<ThreadPoolWatchdogService> logger,
        IThreadPoolMetrics? metrics = null)
    {
        _options = options.Value;
        _logger = logger;
        _metrics = metrics ?? new SystemThreadPoolMetrics();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the gateway time to start up before monitoring
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckThreadPool();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ThreadPool watchdog check failed unexpectedly.");
            }

            await Task.Delay(_options.CheckInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Checks threadpool queue depth and emits a diagnostic snapshot if above threshold.
    /// Exposed as internal for testability.
    /// </summary>
    internal void CheckThreadPool()
    {
        var pendingWorkItems = _metrics.PendingWorkItemCount;

        if (pendingWorkItems > _options.QueueDepthThreshold)
        {
            if (!_warningEmitted)
            {
                var counts = _metrics.GetThreadCounts();

                _logger.LogWarning(
                    "ThreadPool queue depth {PendingCount} exceeds threshold {Threshold}. " +
                    "Workers: available={WorkerAvailable}/{WorkerMax} (min={WorkerMin}), " +
                    "IO: available={IoAvailable}/{IoMax} (min={IoMin}). " +
                    "Possible threadpool starvation or deadlock.",
                    pendingWorkItems,
                    _options.QueueDepthThreshold,
                    counts.WorkerAvailable, counts.WorkerMax, counts.WorkerMin,
                    counts.IoAvailable, counts.IoMax, counts.IoMin);

                _warningEmitted = true;
            }
        }
        else
        {
            // Queue depth recovered
            _warningEmitted = false;
        }
    }

    /// <summary>
    /// Resets internal state for testing purposes only.
    /// </summary>
    internal void ResetForTesting() => _warningEmitted = false;
}
