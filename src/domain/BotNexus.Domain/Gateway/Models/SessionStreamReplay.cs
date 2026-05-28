namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Dedicated facade over the outbound-stream reconnect-replay infrastructure for a
/// single <see cref="Session"/>. The gateway is transport-agnostic — channels
/// (SignalR, Teams, TUI, ...) own the actual wire delivery. This peer only
/// records the sequenced outbound stream so a channel that supports reconnect
/// (e.g. a SignalR hub negotiating a new transport after a drop) can replay any
/// gap from the last acknowledged sequence ID. Owns the underlying
/// <see cref="SessionReplayBuffer"/> and stamps <see cref="Session"/>.<c>UpdatedAt</c>
/// on activity that the runtime previously stamped through forwarding methods on
/// <see cref="GatewaySessionRuntime"/>.
/// </summary>
/// <remarks>
/// Reached via <see cref="GatewaySession"/>.<c>StreamReplay</c>. The 8-member
/// stream-replay surface previously exposed on the facade collapses to one peer
/// accessor; the leak through <c>session.ReplayBuffer</c> is closed because the
/// underlying buffer is no longer reachable from the facade. Construction is
/// restricted to <see cref="GatewaySessionRuntime"/> via the <c>internal</c>
/// constructor so the peer is always paired with its owning session.
///
/// Thread-safety: the underlying <see cref="SessionReplayBuffer"/> serialises its
/// own state under an internal lock; the <see cref="Session"/>.<c>UpdatedAt</c>
/// stamp is intentionally written outside any lock, preserving pre-extract
/// behaviour. A pre-existing race with concurrent history-mutating callers under
/// <c>GatewaySessionRuntime._lock</c> is out of scope for this extract — fixing
/// it would expand the refactor into behavioural concurrency work.
/// </remarks>
public sealed class SessionStreamReplay
{
    private readonly SessionReplayBuffer _buffer;
    private readonly Session _session;

    internal SessionStreamReplay(SessionReplayBuffer buffer, Session session)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Gets the next outbound-stream sequence ID for reconnect replay. The
    /// sequence is transport-agnostic and is consumed by whichever channel
    /// adapter is currently delivering this session's outbound payloads.
    /// The value is rehydrated from persistence through <see cref="SetState"/>;
    /// there is no public setter, so <see cref="SetState"/> is the single
    /// mutation surface for state restore.
    /// </summary>
    public long NextSequenceId => _buffer.NextSequenceId;

    /// <summary>
    /// Atomically allocates and returns the next outbound sequence ID. Also
    /// stamps <see cref="Session"/>.<c>UpdatedAt</c> so replay activity counts
    /// as session activity for downstream warmup/eviction.
    /// </summary>
    public long AllocateSequenceId()
    {
        var sequenceId = _buffer.AllocateSequenceId();
        _session.UpdatedAt = DateTimeOffset.UtcNow;
        return sequenceId;
    }

    /// <summary>
    /// Records a sequenced outbound payload into the bounded replay log. Also
    /// stamps <see cref="Session"/>.<c>UpdatedAt</c>.
    /// </summary>
    /// <param name="sequenceId">The sequence id allocated for this payload.</param>
    /// <param name="payloadJson">The serialised outbound payload.</param>
    /// <param name="replayWindowSize">Maximum number of events retained in the buffer.</param>
    public void AddEvent(long sequenceId, string payloadJson, int replayWindowSize)
    {
        _buffer.AddStreamEvent(sequenceId, payloadJson, replayWindowSize);
        _session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Returns replay entries after <paramref name="lastSequenceId"/>, bounded
    /// by <paramref name="maxReplayCount"/>.
    /// </summary>
    public IReadOnlyList<GatewaySessionStreamEvent> GetEventsAfter(long lastSequenceId, int maxReplayCount)
        => _buffer.GetStreamEventsAfter(lastSequenceId, maxReplayCount);

    /// <summary>
    /// Returns a safe snapshot of the replay entries.
    /// </summary>
    public IReadOnlyList<GatewaySessionStreamEvent> GetEventSnapshot()
        => _buffer.GetStreamEventSnapshot();

    /// <summary>
    /// Replaces replay state from persisted storage. The only mutation surface
    /// for rehydration — there is no public <see cref="NextSequenceId"/>
    /// setter so persistence loaders must funnel through this method.
    /// </summary>
    public void SetState(long nextSequenceId, IEnumerable<GatewaySessionStreamEvent>? streamEvents)
        => _buffer.SetState(nextSequenceId, streamEvents);
}
