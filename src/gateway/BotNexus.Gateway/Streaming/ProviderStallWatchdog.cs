using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Streaming;

/// <summary>
/// Wraps an <see cref="IAsyncEnumerable{AgentStreamEvent}"/> with an inactivity timeout.
/// If no events are yielded within <see cref="InactivityTimeout"/>, the watchdog
/// synthesizes an error event and terminates the stream.
/// </summary>
public sealed class ProviderStallWatchdog
{
    /// <summary>
    /// Default inactivity timeout before the watchdog fires.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates a new watchdog with the specified inactivity timeout.
    /// </summary>
    /// <param name="inactivityTimeout">
    /// Maximum duration to wait for the next event before considering the provider stalled.
    /// Defaults to 90 seconds if null.
    /// </param>
    public ProviderStallWatchdog(TimeSpan? inactivityTimeout = null)
    {
        _timeout = inactivityTimeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout), "Timeout must be positive.");
    }

    /// <summary>
    /// The configured inactivity timeout.
    /// </summary>
    public TimeSpan InactivityTimeout => _timeout;

    /// <summary>
    /// Wraps the given stream with stall detection. If the upstream produces no event
    /// within <see cref="InactivityTimeout"/>, a single <see cref="AgentStreamEventType.Error"/>
    /// event is yielded and the stream terminates.
    /// </summary>
    /// <param name="source">The upstream agent event stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable that yields events until stall or completion.</returns>
    public IAsyncEnumerable<AgentStreamEvent> WrapAsync(
        IAsyncEnumerable<AgentStreamEvent> source,
        CancellationToken cancellationToken = default)
    {
        return new WatchdogEnumerable(source, _timeout, cancellationToken);
    }

    private sealed class WatchdogEnumerable(
        IAsyncEnumerable<AgentStreamEvent> source,
        TimeSpan timeout,
        CancellationToken cancellationToken) : IAsyncEnumerable<AgentStreamEvent>
    {
        public IAsyncEnumerator<AgentStreamEvent> GetAsyncEnumerator(CancellationToken token = default)
        {
            // Prefer the token passed at enumeration time if not None, else fall back to construction token.
            var effective = token != default ? token : cancellationToken;
            return new WatchdogEnumerator(source.GetAsyncEnumerator(effective), timeout, effective);
        }
    }

    private sealed class WatchdogEnumerator : IAsyncEnumerator<AgentStreamEvent>
    {
        /// <summary>
        /// How long <see cref="DisposeAsync"/> waits for an abandoned upstream step to settle before
        /// giving up and leaving the iterator undisposed. Short by design: disposal sits on the
        /// caller's teardown path, and the whole reason we are here is that upstream is unresponsive.
        /// </summary>
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

        private readonly IAsyncEnumerator<AgentStreamEvent> _source;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _ct;
        private AgentStreamEvent? _current;
        private bool _done;

        // The upstream MoveNextAsync we stopped waiting on when the stall timeout (or external
        // cancellation) fired. WaitAsync abandons the *wrapper*, but the underlying iterator step is
        // still running: it owns a ManualResetValueTaskSourceCore whose continuation will complete on
        // some later thread. Disposing the iterator while that step is in flight is a contract
        // violation that resets the source's version token, so the late continuation calls
        // GetStatus(staleToken) and throws InvalidOperationException on a ThreadPool worker with no
        // one to catch it -- which kills the whole process rather than failing anything catchable.
        // Holding the task here lets DisposeAsync drain it first (#2970).
        private Task<bool>? _pendingMoveNext;

        public WatchdogEnumerator(IAsyncEnumerator<AgentStreamEvent> source, TimeSpan timeout, CancellationToken ct)
        {
            _source = source;
            _timeout = timeout;
            _ct = ct;
        }

        public AgentStreamEvent Current => _current ?? throw new InvalidOperationException("No current element.");

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_done)
                return false;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                timeoutCts.CancelAfter(_timeout);

                // Materialise the step once and keep the reference: if WaitAsync gives up on it we
                // still need to be able to drain it in DisposeAsync.
                var step = _pendingMoveNext ?? _source.MoveNextAsync().AsTask();
                _pendingMoveNext = step;

                var moved = await step.WaitAsync(timeoutCts.Token);

                // Completed normally, so there is nothing left in flight to drain.
                _pendingMoveNext = null;

                if (!moved)
                {
                    _done = true;
                    return false;
                }

                _current = _source.Current;
                return true;
            }
            catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
            {
                // Timeout fired (not external cancellation). Yield error on next call.
                _current = new AgentStreamEvent
                {
                    Type = AgentStreamEventType.Error,
                    ErrorMessage = $"Provider stall detected: no response received for {_timeout.TotalSeconds:F0} seconds. The provider may have dropped the connection."
                };
                _done = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                // External cancellation — just stop.
                _done = true;
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Drain any abandoned MoveNextAsync BEFORE disposing the source. An async iterator may not
            // be disposed while a step is pending; doing so corrupts its value-task source and surfaces
            // as an unhandled InvalidOperationException on a ThreadPool thread (i.e. process death).
            // We must not wait unboundedly here -- the step we abandoned may be an infinite or very slow
            // producer, and that is precisely why the watchdog fired -- so bound the drain and, if it
            // does not settle, deliberately leave the iterator undisposed and simply observe the task's
            // eventual exception. A leaked-but-inert iterator is strictly better than a dead process.
            var pending = _pendingMoveNext;
            _pendingMoveNext = null;

            if (pending is not null && !pending.IsCompleted)
            {
                var settled = await Task.WhenAny(pending, Task.Delay(DrainTimeout)).ConfigureAwait(false);
                if (!ReferenceEquals(settled, pending))
                {
                    // Still running. Never touch the iterator again; just make sure the task's exception
                    // (if any) is observed so it cannot resurface as an unobserved-exception crash.
                    ObserveQuietly(pending);
                    return;
                }
            }

            // Observe a faulted/cancelled drain without rethrowing: disposal must not raise the very
            // error the watchdog exists to contain.
            if (pending is not null)
                ObserveQuietly(pending);

            try
            {
                await _source.DisposeAsync();
            }
            catch (NotSupportedException)
            {
                // Some async iterators throw NotSupportedException when disposed
                // while their MoveNextAsync is still pending (e.g. mid-Task.Delay).
                // This is safe to swallow — the iterator will be GC'd.
            }
        }

        /// <summary>
        /// Attaches a no-op continuation so a faulted task's exception is considered observed and never
        /// reaches <see cref="TaskScheduler.UnobservedTaskException"/>.
        /// </summary>
        private static void ObserveQuietly(Task task)
            => _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }
}
