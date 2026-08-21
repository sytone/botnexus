using System.Diagnostics;

namespace BotNexus.Testing;

/// <summary>
/// Coordinates tests with observable conditions instead of fixed sleeps or project-local polling loops.
/// </summary>
internal static class TestAwait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Waits until a synchronous observable condition becomes true, preserving caller cancellation
    /// and reporting the unmet condition when the observation window expires.
    /// </summary>
    public static Task EventuallyAsync(
        Func<bool> condition,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        return EventuallyCoreAsync(
            () => new ValueTask<bool>(condition()),
            description,
            timeout,
            pollInterval,
            cancellationToken,
            delayAsync);
    }

    /// <summary>
    /// Waits until an asynchronous observable condition becomes true, preserving caller cancellation
    /// and reporting the unmet condition when the observation window expires.
    /// </summary>
    public static Task EventuallyAsync(
        Func<Task<bool>> condition,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        return EventuallyCoreAsync(
            async () => await condition().ConfigureAwait(false),
            description,
            timeout,
            pollInterval,
            cancellationToken,
            delayAsync);
    }

    private static async Task EventuallyCoreAsync(
        Func<ValueTask<bool>> condition,
        string description,
        TimeSpan? timeout,
        TimeSpan? pollInterval,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var observationWindow = timeout ?? DefaultTimeout;
        var interval = pollInterval ?? DefaultPollInterval;
        if (observationWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The observation window cannot be negative.");
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "The poll interval must be positive.");

        delayAsync ??= static (delay, token) => Task.Delay(delay, token);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            if (await condition().ConfigureAwait(false))
                return;

            if (stopwatch.Elapsed >= observationWindow)
            {
                var attemptLabel = attempts == 1 ? "attempt" : "attempts";
                throw new TimeoutException(
                    $"Timed out after {observationWindow.TotalSeconds:0.###}s waiting for {description} " +
                    $"({attempts} {attemptLabel}).");
            }

            var remaining = observationWindow - stopwatch.Elapsed;
            await delayAsync(interval <= remaining ? interval : remaining, cancellationToken).ConfigureAwait(false);
        }
    }
}