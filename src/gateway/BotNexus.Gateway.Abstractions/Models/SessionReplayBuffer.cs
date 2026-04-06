namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Thread-safe bounded replay buffer for sequenced WebSocket outbound payloads.
/// </summary>
public sealed class SessionReplayBuffer
{
    public const int DefaultReplayWindowSize = 1000;

    private readonly Lock _lock = new();
    private readonly List<GatewaySessionStreamEvent> _streamEvents = [];
    private long _nextSequenceId = 1;

    /// <summary>
    /// Next WebSocket outbound sequence ID for reconnect replay.
    /// </summary>
    public long NextSequenceId
    {
        get
        {
            lock (_lock)
            {
                return _nextSequenceId;
            }
        }
        set
        {
            lock (_lock)
            {
                _nextSequenceId = value <= 0 ? 1 : value;
            }
        }
    }

    /// <summary>
    /// Atomically allocates and returns the next outbound sequence ID.
    /// </summary>
    public long AllocateSequenceId()
    {
        lock (_lock)
        {
            var sequenceId = _nextSequenceId;
            _nextSequenceId = sequenceId + 1;
            return sequenceId;
        }
    }

    /// <summary>
    /// Records a sequenced outbound payload into the bounded replay log.
    /// </summary>
    public void AddStreamEvent(long sequenceId, string payloadJson, int replayWindowSize = DefaultReplayWindowSize)
    {
        lock (_lock)
        {
            _streamEvents.Add(new GatewaySessionStreamEvent(sequenceId, payloadJson, DateTimeOffset.UtcNow));
            var max = replayWindowSize > 0 ? replayWindowSize : DefaultReplayWindowSize;
            if (_streamEvents.Count > max)
            {
                _streamEvents.RemoveRange(0, _streamEvents.Count - max);
            }
        }
    }

    /// <summary>
    /// Returns replay entries after <paramref name="lastSequenceId"/>, bounded by <paramref name="maxReplayCount"/>.
    /// </summary>
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventsAfter(long lastSequenceId, int maxReplayCount)
    {
        lock (_lock)
        {
            return _streamEvents
                .Where(evt => evt.SequenceId > lastSequenceId)
                .Take(Math.Max(maxReplayCount, 1))
                .ToList();
        }
    }

    /// <summary>
    /// Returns a safe snapshot of replay entries.
    /// </summary>
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventSnapshot()
    {
        lock (_lock)
        {
            return _streamEvents.ToList();
        }
    }

    /// <summary>
    /// Replaces replay state from persisted storage.
    /// </summary>
    public void SetState(long nextSequenceId, IEnumerable<GatewaySessionStreamEvent>? streamEvents)
    {
        lock (_lock)
        {
            _nextSequenceId = nextSequenceId <= 0 ? 1 : nextSequenceId;
            _streamEvents.Clear();
            if (streamEvents is not null)
            {
                _streamEvents.AddRange(streamEvents.OrderBy(evt => evt.SequenceId));
            }
        }
    }
}
