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
    /// Waits for a fixture or production callback to raise a signal under the shared generous
    /// deadline. Only a deadline expiry is rewritten; exceptions produced by the signal itself
    /// retain their original type and diagnostic.
    /// </summary>
    public static async Task SignaledAsync(
        Task signal,
        string description,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var window = timeout ?? DefaultTimeout;
        try
        {
            await signal.WaitAsync(window, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!signal.IsCompleted)
        {
            throw new TimeoutException(
                $"Timed out after {window.TotalSeconds:0.###}s waiting for {description}. The signal was " +
                "never raised, so the code under test did not reach the point that raises it.");
        }
    }

    /// <summary>
    /// Waits for a value-carrying signal under the shared generous deadline.
    /// </summary>
    public static async Task<T> SignaledAsync<T>(
        Task<T> signal,
        string description,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var window = timeout ?? DefaultTimeout;
        try
        {
            return await signal.WaitAsync(window, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!signal.IsCompleted)
        {
            throw new TimeoutException(
                $"Timed out after {window.TotalSeconds:0.###}s waiting for {description}. The signal was " +
                "never raised, so the code under test did not reach the point that raises it.");
        }
    }

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