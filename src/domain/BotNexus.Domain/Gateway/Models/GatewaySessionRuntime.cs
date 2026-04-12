namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Infrastructure runtime wrapper for <see cref="Session"/> that provides thread-safe mutation
/// and replay-buffer behavior used by gateway real-time delivery paths.
/// </summary>
public sealed class GatewaySessionRuntime
{
    private readonly Lock _lock = new();
    private readonly SessionReplayBuffer _replayBuffer = new();

    public GatewaySessionRuntime(Session session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Session Session { get; }

    public long NextSequenceId
    {
        get => _replayBuffer.NextSequenceId;
        set => _replayBuffer.NextSequenceId = value;
    }

    public List<GatewaySessionStreamEvent> StreamEventLog => [.. _replayBuffer.GetStreamEventSnapshot()];

    public SessionReplayBuffer ReplayBuffer => _replayBuffer;

    public void AddEntry(SessionEntry entry)
    {
        lock (_lock)
        {
            Session.History.Add(entry);
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void AddEntries(IEnumerable<SessionEntry> entries)
    {
        lock (_lock)
        {
            Session.History.AddRange(entries);
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void ReplaceHistory(IReadOnlyList<SessionEntry> compactedEntries)
    {
        lock (_lock)
        {
            Session.History.Clear();
            Session.History.AddRange(compactedEntries);
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyList<SessionEntry> GetHistorySnapshot()
    {
        lock (_lock)
        {
            return Session.History.ToList();
        }
    }

    public IReadOnlyList<SessionEntry> GetHistorySnapshot(int offset, int limit)
    {
        lock (_lock)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be greater than or equal to zero.");
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than or equal to zero.");

            return Session.History
                .Skip(offset)
                .Take(limit)
                .ToList();
        }
    }

    public long AllocateSequenceId()
    {
        var sequenceId = _replayBuffer.AllocateSequenceId();
        Session.UpdatedAt = DateTimeOffset.UtcNow;
        return sequenceId;
    }

    public void AddStreamEvent(long sequenceId, string payloadJson, int replayWindowSize)
    {
        _replayBuffer.AddStreamEvent(sequenceId, payloadJson, replayWindowSize);
        Session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventsAfter(long lastSequenceId, int maxReplayCount)
        => _replayBuffer.GetStreamEventsAfter(lastSequenceId, maxReplayCount);

    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventSnapshot()
        => _replayBuffer.GetStreamEventSnapshot();

    public void SetStreamReplayState(long nextSequenceId, IEnumerable<GatewaySessionStreamEvent>? streamEvents)
        => _replayBuffer.SetState(nextSequenceId, streamEvents);
}
