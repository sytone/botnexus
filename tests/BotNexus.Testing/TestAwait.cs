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
    /// Waits for a signal a test fixture raises — a <see cref="TaskCompletionSource"/> completed from
    /// a fake, a mock callback, or a production callback the test subscribed to — under one generous,
    /// centrally-owned deadline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefer this over writing <c>signal.WaitAsync(TimeSpan.FromSeconds(n))</c> by hand. The signal
    /// is already the synchronisation primitive; the deadline exists only so a genuine hang is
    /// reported rather than stalling the run forever. It is therefore never reached on the passing
    /// path, which means making it generous costs nothing and making it tight buys nothing except
    /// failures on a loaded CI runner. Hand-written five-second deadlines took unrelated PRs red
    /// three times (#75, #103 and <c>main</c> at 6c215e2c) for exactly that reason.
    /// </para>
    /// <para>
    /// The exception is a wait whose EXPIRY is the assertion — "prove this does not complete". There
    /// the deadline is reached on the passing path, so it must stay short and must be justified at
    /// the call site; see <c>TestObservationWindowTests</c> for how that case is fenced.
    /// </para>
    /// <para>
    /// The <c>when (!signal.IsCompleted)</c> guard matters more than it looks. Only the DEADLINE's
    /// own <see cref="TimeoutException"/> should be rewritten; a <see cref="TimeoutException"/>
    /// raised by the awaited work itself must reach the test untouched. Production types derive from
    /// it — <c>StripeLockTimeoutException</c> names the key and the bound it exceeded — and swallowing
    /// one to report "the signal was never raised" would replace a precise diagnostic with a wrong
    /// one. If the signal has completed, the exception came from the signal, not from this wait.
    /// </para>
    /// </remarks>
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
    /// Waits for a signal that carries a value, under the same generous deadline as
    /// <see cref="SignaledAsync(Task, string, TimeSpan?, CancellationToken)"/>.
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