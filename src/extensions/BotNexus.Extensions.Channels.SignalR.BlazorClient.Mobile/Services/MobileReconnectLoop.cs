namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

/// <summary>
/// The mobile reconnect overlay's retry loop, extracted from <c>ReconnectOverlay.razor</c> so the
/// long-outage recovery sequence is unit-testable without timers or wall-clock waits (#2625).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this loop is the only thing left running.</strong> The mobile hub is built with
/// <see cref="MobileReconnectRetryPolicy"/>, which never returns <see langword="null"/> and would
/// retry forever -- but only for as long as the underlying <c>HubConnection</c> that owns it is
/// alive. The resume path (<c>PortalLoadService.RebuildConnectionAsync</c>) replaces that
/// connection wholesale, and SignalR does <em>not</em> run automatic reconnect on a connection
/// whose <c>StartAsync</c> never succeeded. So from the first failed rebuild onwards this loop is
/// the sole recovery mechanism (#2625 AC3). That is stated deliberately rather than left implicit:
/// two independent retry mechanisms against one connection was the structural defect.
/// </para>
///
/// <para>
/// <strong>Success is not "did not throw".</strong> A resume can complete normally without having
/// restored the connection -- the liveness probe can report alive on a socket the gateway has since
/// dropped, and the rebuild branch swallows nothing but still depends on a fresh negotiate landing.
/// The loop therefore consults <c>isConnected</c> after every attempt and keeps retrying until that
/// predicate is genuinely true. This is what distinguishes it from the desktop terminal-close loop
/// (#2624), where a non-throwing dial IS the success signal, and is why the two are separate types
/// beyond the fact that Core cannot reference the Mobile assembly.
/// </para>
///
/// <para>
/// <strong>Rate is bounded, duration is not.</strong> Delays follow
/// <see cref="MobileReconnectBackoff"/> (2s, 4s, 8s, 16s, then held at 30s) with no attempt limit
/// and no total-duration limit -- a <c>botnexus update</c> takes minutes and an update that hits a
/// problem takes far longer, so any finite budget is just a longer cliff. The cap is load-bearing in
/// both directions: uncapped exponential would mean a gateway that returns after 45 minutes goes
/// unnoticed for a further long interval (the reported symptom in a new form), while no growth would
/// be a tight loop against a down gateway. Worst-case detection latency is exactly one cap interval.
/// </para>
///
/// <para>
/// <strong>The attempt counter is never reset mid-outage.</strong> It advances monotonically until a
/// resume genuinely reconnects, and only then resets for the next outage. Resetting on failure --
/// including on a user's "Retry now" tap -- would make the ceiling inert and turn the loop into a
/// fixed-rate poll (the defect pattern from #2564).
/// </para>
///
/// <para>
/// Time is injected as a delay seam, so tests assert the <em>durations requested</em> rather than
/// measuring elapsed time.
/// </para>
/// </remarks>
public sealed class MobileReconnectLoop : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _resumeAsync;
    private readonly Func<bool> _isConnected;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Raised whenever <see cref="IsRetrying"/> changes so the overlay can re-render.</summary>
    public event Action? OnRetryStateChanged;

    /// <summary>True while the loop is actively retrying an outage.</summary>
    public bool IsRetrying { get; private set; }

    /// <summary>
    /// Resume attempts made during the current outage. Resets to zero only when a resume genuinely
    /// reconnects, or when the loop is stopped -- never on a failure, and never on a manual retry.
    /// </summary>
    public int AttemptCount { get; private set; }

    /// <summary>Creates the loop.</summary>
    /// <param name="resumeAsync">
    /// Performs one liveness-verified resume (probe-then-rebuild, #1838). May throw; a throw is
    /// treated exactly like a completed-but-still-disconnected attempt.
    /// </param>
    /// <param name="isConnected">
    /// Authoritative post-attempt check. The loop keeps going until this returns <see langword="true"/>,
    /// so an attempt that completes without actually restoring the socket does not end the outage.
    /// </param>
    /// <param name="delayAsync">
    /// Delay seam; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. Tests supply a
    /// recorder so they can assert requested durations without sleeping.
    /// </param>
    public MobileReconnectLoop(
        Func<CancellationToken, Task> resumeAsync,
        Func<bool> isConnected,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _resumeAsync = resumeAsync ?? throw new ArgumentNullException(nameof(resumeAsync));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _delayAsync = delayAsync ?? Task.Delay;
    }

    /// <summary>
    /// Starts retrying if not already running. Idempotent: repeated disconnect notifications during a
    /// single outage join the in-flight loop rather than stacking parallel resume loops, which would
    /// multiply the request rate against a gateway that is already down.
    /// </summary>
    public void Start()
    {
        if (_disposed)
            return;

        lock (_sync)
        {
            if (_loop is { IsCompleted: false })
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            AttemptCount = 0;
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }

        // Raised outside the lock so a UI handler cannot re-enter Start under it.
        SetRetrying(true);
    }

    /// <summary>
    /// Stops the loop and awaits its exit so no resume is in flight when this returns. Used on
    /// teardown and when the connection is restored by some other path.
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            loop = _loop;
            cts = _cts;
            _loop = null;
            AttemptCount = 0;
        }

        if (cts is not null)
            await cts.CancelAsync();

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is the normal stop path.
            }
        }

        SetRetrying(false);
    }

    /// <summary>
    /// Runs one resume attempt immediately, out of band with the backoff schedule (the user's
    /// "Retry now" tap). Deliberately does <strong>not</strong> reset <see cref="AttemptCount"/>:
    /// a reset would let repeated taps hold the schedule at its base interval, which is exactly the
    /// inert-ceiling shape from #2564.
    /// </summary>
    /// <returns><see langword="true"/> when the attempt restored the connection.</returns>
    public async Task<bool> RetryNowAsync(CancellationToken cancellationToken = default)
    {
        var reconnected = await AttemptAsync(cancellationToken);
        if (reconnected)
            await StopAsync();

        return reconnected;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // The delay comes FIRST so attempt N always waits GetDelay(N) -- the exact schedule a
            // test can assert -- and so a retry never races the gateway's own shutdown.
            var delay = MobileReconnectBackoff.GetDelay(AttemptCount);

            try
            {
                await _delayAsync(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            // Incremented BEFORE the attempt and never rolled back, so the backoff keeps climbing to
            // the cap for as long as the outage lasts.
            AttemptCount++;

            bool reconnected;
            try
            {
                reconnected = await AttemptAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (reconnected)
            {
                // The socket is back AND the resume path has re-run SubscribeAll/session
                // re-registration, so working state -- not just the transport -- is restored.
                AttemptCount = 0;
                SetRetrying(false);
                return;
            }
        }

        SetRetrying(false);
    }

    /// <summary>
    /// One resume attempt. A throw is swallowed on purpose: a failed resume against a gateway that is
    /// still restarting is the EXPECTED case, not an error condition, and letting it escape would end
    /// the loop and restore the very defect being fixed. The return value comes from
    /// <c>isConnected</c>, not from "did not throw".
    /// </summary>
    private async Task<bool> AttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _resumeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fall through to the connectivity check below: even a throwing resume can have left the
            // connection up, and a non-throwing one can have left it down. Only isConnected decides.
        }

        return _isConnected();
    }

    private void SetRetrying(bool value)
    {
        if (IsRetrying == value)
            return;

        IsRetrying = value;
        OnRetryStateChanged?.Invoke();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync();
        _cts?.Dispose();
    }
}
