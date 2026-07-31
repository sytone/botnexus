namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Re-dial loop that runs after SignalR's automatic-reconnect budget is spent and <c>Closed</c> has
/// been raised, so the desktop portal recovers from a gateway restart without a browser reload (#2624).
/// </summary>
/// <remarks>
/// <para>
/// SignalR's automatic reconnect only operates <em>within</em> its retry budget. Once that budget is
/// exhausted the connection enters the terminal <c>Disconnected</c> state, <c>Reconnected</c> can
/// never fire again, and the whole reconnect-recovery path becomes unreachable for the rest of the
/// page's lifetime. Widening the budget only moves that cliff; this loop removes it, by re-dialling
/// from the terminal state for as long as the page is open.
/// </para>
///
/// <para>
/// The loop has <strong>no attempt limit and no total-duration limit</strong>. A
/// <c>botnexus update</c> takes minutes, and an update that hits a problem can take far longer, so
/// any finite budget is just a longer cliff. Rate is bounded instead of duration: delays follow
/// <see cref="DesktopReconnectBackoff"/> (2s, 4s, 8s, 16s, then held at 30s), so a multi-hour outage
/// settles into infrequent polling and worst-case detection latency after the gateway returns is one
/// cap interval.
/// </para>
///
/// <para>
/// The attempt counter is <strong>never reset mid-outage</strong>. It advances monotonically until a
/// dial succeeds, at which point the loop stops entirely and the counter is reset for the next
/// outage. Resetting it on each failure would make the cap inert and turn the loop into a tight
/// poll (the defect pattern from #2564).
/// </para>
///
/// <para>
/// Time is injected as a delay seam rather than taken from the ambient clock, so tests assert the
/// <em>durations requested</em> instead of wall-clock elapsed time.
/// </para>
/// </remarks>
public sealed class DesktopReconnectLoop : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _dialAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<string, Exception?>? _log;

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Raised whenever <see cref="IsReconnecting"/> changes, so the connection indicator can
    /// distinguish an outage that is actively being retried from a dead page.
    /// </summary>
    public event Action? OnReconnectStateChanged;

    /// <summary>
    /// True while the loop is actively re-dialling after a terminal close. Drives the portal's
    /// "Reconnecting" indicator: without it the UI would show a bare "Disconnected" and a silently
    /// retrying portal is only marginally more legible than a dead one.
    /// </summary>
    public bool IsReconnecting { get; private set; }

    /// <summary>
    /// The number of re-dial attempts made during the current outage. Exposed for diagnostics and
    /// tests; resets to zero only when a dial succeeds or the loop is stopped.
    /// </summary>
    public int AttemptCount { get; private set; }

    /// <summary>
    /// Creates the loop.
    /// </summary>
    /// <param name="dialAsync">
    /// Performs one full reconnect attempt: rebuild the hub connection and run the existing
    /// reconnect-recovery path. Must throw on failure so the loop knows to back off and retry.
    /// </param>
    /// <param name="delayAsync">
    /// Delay seam. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>; tests supply a
    /// recorder so they can assert the requested durations without sleeping.
    /// </param>
    /// <param name="log">Optional diagnostics sink invoked with a message and optional exception.</param>
    public DesktopReconnectLoop(
        Func<CancellationToken, Task> dialAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<string, Exception?>? log = null)
    {
        _dialAsync = dialAsync ?? throw new ArgumentNullException(nameof(dialAsync));
        _delayAsync = delayAsync ?? Task.Delay;
        _log = log;
    }

    /// <summary>
    /// Starts re-dialling if not already running. Idempotent: repeated <c>Closed</c> notifications
    /// during a single outage join the in-flight loop rather than stacking parallel dial loops, which
    /// would multiply the request rate against a gateway that is already down.
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
        SetReconnecting(true);
    }

    /// <summary>
    /// Stops the loop (page teardown, or an external path that re-established the connection) and
    /// awaits its exit so no dial is in flight when this returns.
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

        SetReconnecting(false);
    }

    /// <summary>
    /// The loop body. Exposed internally so a test can drive it deterministically with a fake delay.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // The delay comes FIRST so an immediate re-dial never races the gateway's own shutdown,
            // and so attempt N always waits GetDelay(N) -- the schedule tests can assert directly.
            var delay = DesktopReconnectBackoff.GetDelay(AttemptCount);

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

            AttemptCount++;

            try
            {
                await _dialAsync(cancellationToken);

                // Success: the socket is back AND the caller's dial delegate has already run the
                // existing recovery path, so the loop's job is done for this outage.
                _log?.Invoke($"Desktop portal reconnected after {AttemptCount} attempt(s).", null);
                AttemptCount = 0;
                SetReconnecting(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Deliberately swallowed and retried: a failed dial against a gateway that is still
                // restarting is the EXPECTED case, not an error condition. The loop must not exit --
                // exiting here would restore exactly the terminal-close defect this class removes.
                // AttemptCount is NOT reset, so the backoff keeps growing to the cap.
                _log?.Invoke($"Desktop portal reconnect attempt {AttemptCount} failed; retrying.", ex);
            }
        }

        SetReconnecting(false);
    }

    private void SetReconnecting(bool value)
    {
        if (IsReconnecting == value)
            return;

        IsReconnecting = value;
        OnReconnectStateChanged?.Invoke();
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
