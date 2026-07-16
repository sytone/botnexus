using System.Collections.Concurrent;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Thread-safe, reference-counted <see cref="ISessionTurnTracker"/>. A session is "live" while
/// its counter is positive; nested turns (rare, but possible under re-entrancy) increment the
/// counter and each scope disposal decrements it. When the counter reaches zero the entry is
/// removed so the dictionary does not grow unbounded across a long-lived gateway process.
/// </summary>
public sealed class SessionTurnTracker : ISessionTurnTracker
{
    private readonly ConcurrentDictionary<string, int> _liveCounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IDisposable BeginTurn(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _liveCounts.AddOrUpdate(sessionId, 1, static (_, current) => current + 1);
        return new TurnScope(this, sessionId);
    }

    /// <inheritdoc />
    public bool HasLiveTurn(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _liveCounts.TryGetValue(sessionId, out var count) && count > 0;
    }

    private void EndTurn(string sessionId)
    {
        // Decrement under the concurrent dictionary's per-key atomicity; remove the entry
        // when it hits zero so a busy gateway does not accumulate one key per session forever.
        while (true)
        {
            if (!_liveCounts.TryGetValue(sessionId, out var current))
                return;

            if (current <= 1)
            {
                // Remove only if still at the value we observed; otherwise loop and retry.
                if (((ICollection<KeyValuePair<string, int>>)_liveCounts)
                    .Remove(new KeyValuePair<string, int>(sessionId, current)))
                    return;
            }
            else if (_liveCounts.TryUpdate(sessionId, current - 1, current))
            {
                return;
            }
        }
    }

    private sealed class TurnScope(SessionTurnTracker owner, string sessionId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Idempotent: guard against double-dispose decrementing the counter twice.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndTurn(sessionId);
        }
    }
}
