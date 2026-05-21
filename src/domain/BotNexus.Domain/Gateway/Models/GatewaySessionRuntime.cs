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

    /// <summary>
    /// Gets the session.
    /// </summary>
    public Session Session { get; }

    public long NextSequenceId
    {
        get => _replayBuffer.NextSequenceId;
        set => _replayBuffer.NextSequenceId = value;
    }

    public List<GatewaySessionStreamEvent> StreamEventLog => [.. _replayBuffer.GetStreamEventSnapshot()];

    public SessionReplayBuffer ReplayBuffer => _replayBuffer;

    /// <summary>
    /// Executes add entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void AddEntry(SessionEntry entry)
    {
        lock (_lock)
        {
            Session.History.Add(entry);
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Executes add entries.
    /// </summary>
    /// <param name="entries">The entries.</param>
    public void AddEntries(IEnumerable<SessionEntry> entries)
    {
        lock (_lock)
        {
            Session.History.AddRange(entries);
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Executes replace history.
    /// </summary>
    /// <param name="compactedEntries">The compacted entries.</param>
    public void ReplaceHistory(IReadOnlyList<SessionEntry> compactedEntries)
    {
        lock (_lock)
        {
            Session.History.Clear();
            Session.History.AddRange(compactedEntries);
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Removes any crash-sentinel entries from the session history.
    /// Called on clean turn completion to ensure the sentinel does not persist
    /// as a real history entry after a successful run (#363).
    /// </summary>
    public void RemoveCrashSentinels()
    {
        lock (_lock)
        {
            Session.History.RemoveAll(static e => e.IsCrashSentinel);
        }
    }

    /// <summary>
    /// Executes get history snapshot.
    /// </summary>
    /// <returns>The get history snapshot result.</returns>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot()
    {
        lock (_lock)
        {
            return Session.History.ToList();
        }
    }

    /// <summary>
    /// Executes get history snapshot.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <param name="limit">The limit.</param>
    /// <returns>The get history snapshot result.</returns>
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

    /// <summary>
    /// Executes allocate sequence id.
    /// </summary>
    /// <returns>The allocate sequence id result.</returns>
    public long AllocateSequenceId()
    {
        var sequenceId = _replayBuffer.AllocateSequenceId();
        Session.UpdatedAt = DateTimeOffset.UtcNow;
        return sequenceId;
    }

    /// <summary>
    /// Executes add stream event.
    /// </summary>
    /// <param name="sequenceId">The sequence id.</param>
    /// <param name="payloadJson">The payload json.</param>
    /// <param name="replayWindowSize">The replay window size.</param>
    public void AddStreamEvent(long sequenceId, string payloadJson, int replayWindowSize)
    {
        _replayBuffer.AddStreamEvent(sequenceId, payloadJson, replayWindowSize);
        Session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Executes get stream events after.
    /// </summary>
    /// <param name="lastSequenceId">The last sequence id.</param>
    /// <param name="maxReplayCount">The max replay count.</param>
    /// <returns>The get stream events after result.</returns>
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventsAfter(long lastSequenceId, int maxReplayCount)
        => _replayBuffer.GetStreamEventsAfter(lastSequenceId, maxReplayCount);

    /// <summary>
    /// Executes get stream event snapshot.
    /// </summary>
    /// <returns>The get stream event snapshot result.</returns>
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventSnapshot()
        => _replayBuffer.GetStreamEventSnapshot();

    /// <summary>
    /// Executes set stream replay state.
    /// </summary>
    /// <param name="nextSequenceId">The next sequence id.</param>
    /// <param name="streamEvents">The stream events.</param>
    public void SetStreamReplayState(long nextSequenceId, IEnumerable<GatewaySessionStreamEvent>? streamEvents)
        => _replayBuffer.SetState(nextSequenceId, streamEvents);
}
