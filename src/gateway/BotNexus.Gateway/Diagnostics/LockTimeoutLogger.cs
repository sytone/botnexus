using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Wraps semaphore acquisition with timeout-based warning logging.
/// If acquiring a lock takes longer than the configured threshold, a warning is emitted
/// with the lock name and elapsed time — helping identify lock contention and deadlock sources.
/// </summary>
public sealed class LockTimeoutLogger(
    ILogger<LockTimeoutLogger> logger,
    TimeSpan warningThreshold)
{
    private readonly ILogger<LockTimeoutLogger> _logger = logger;
    private readonly TimeSpan _warningThreshold = warningThreshold;

    /// <summary>
    /// Creates a LockTimeoutLogger with the default 5-second warning threshold.
    /// </summary>
    public LockTimeoutLogger(ILogger<LockTimeoutLogger> logger)
        : this(logger, TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>
    /// Acquires the semaphore and returns a disposable handle. If acquisition takes longer
    /// than the warning threshold, a WARNING log is emitted with the lock name and elapsed time.
    /// </summary>
    /// <param name="semaphore">The semaphore to acquire.</param>
    /// <param name="lockName">A descriptive name for the lock (for logging).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A disposable that releases the semaphore on dispose.</returns>
    public async Task<IDisposable> AcquireAsync(SemaphoreSlim semaphore, string lockName, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // Try to acquire immediately
        if (semaphore.Wait(0))
        {
            return new SemaphoreReleaser(semaphore);
        }

        // Start monitoring for slow acquisition
        using var warningCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var warningTask = EmitWarningAfterDelay(lockName, sw, warningCts.Token);

        await semaphore.WaitAsync(cancellationToken);

        // Cancel the warning task if we acquired in time
        await warningCts.CancelAsync();

        sw.Stop();
        if (sw.Elapsed >= _warningThreshold)
        {
            _logger.LogWarning(
                "Lock acquisition for '{LockName}' took {Elapsed}ms (threshold: {Threshold}ms). " +
                "Possible contention or deadlock.",
                lockName,
                sw.ElapsedMilliseconds,
                _warningThreshold.TotalMilliseconds);
        }

        return new SemaphoreReleaser(semaphore);
    }

    private async Task EmitWarningAfterDelay(string lockName, Stopwatch sw, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_warningThreshold, ct);

            // If we get here, the threshold was exceeded while still waiting
            _logger.LogWarning(
                "Lock acquisition for '{LockName}' is taking longer than {Threshold}ms (still waiting after {Elapsed}ms).",
                lockName,
                _warningThreshold.TotalMilliseconds,
                sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Expected: either acquired or outer cancellation
        }
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
