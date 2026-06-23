namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// A bounded, thread-safe, in-memory <see cref="ISecurityEventSink"/> backed by a fixed-size
/// circular buffer. When the buffer is full, recording a new event evicts the oldest one
/// (it never blocks). <see cref="Snapshot"/> returns the retained events most-recent first.
/// </summary>
/// <remarks>
/// Step 1/5 of the security-event taxonomy (#1532, part of #1526). This is the default trusted
/// sink: it keeps a recent window of security events for the (future) trusted diagnostics surface
/// without unbounded memory growth on a long-running gateway. It is intentionally separate from
/// the public <c>LogDiagnosticsRingBuffer</c> so trusted security events never leak onto the
/// public diagnostic stream.
/// </remarks>
public sealed class RingBufferSecurityEventSink : ISecurityEventSink
{
    private readonly SecurityEvent[] _buffer;
    private readonly Lock _gate = new();

    // Index of the next write slot. _count tracks how many slots are populated.
    private int _next;
    private int _count;

    /// <summary>
    /// Creates a new ring-buffer sink.
    /// </summary>
    /// <param name="capacity">Maximum number of events to retain. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is not positive.</exception>
    public RingBufferSecurityEventSink(int capacity = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = new SecurityEvent[capacity];
    }

    /// <inheritdoc />
    public void Record(SecurityEvent securityEvent)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);

        lock (_gate)
        {
            _buffer[_next] = securityEvent;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SecurityEvent> Snapshot()
    {
        lock (_gate)
        {
            var result = new SecurityEvent[_count];
            // Walk backwards from the most recently written slot so the result is most-recent first.
            // The most recent write landed at (_next - 1); older entries precede it (wrapping).
            var idx = _next - 1;
            for (var i = 0; i < _count; i++)
            {
                if (idx < 0)
                    idx += _buffer.Length;
                result[i] = _buffer[idx];
                idx--;
            }

            return result;
        }
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _next = 0;
            _count = 0;
        }
    }
}
