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
        private readonly IAsyncEnumerator<AgentStreamEvent> _source;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _ct;
        private AgentStreamEvent? _current;
        private bool _done;

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

                var moved = await _source.MoveNextAsync().AsTask().WaitAsync(timeoutCts.Token);
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
    }
}
