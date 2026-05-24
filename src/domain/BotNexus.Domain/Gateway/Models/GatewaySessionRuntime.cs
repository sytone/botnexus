namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Infrastructure runtime wrapper for <see cref="Session"/> that provides thread-safe mutation
/// and replay-buffer behavior used by gateway real-time delivery paths.
/// </summary>
public sealed class GatewaySessionRuntime
{
    private readonly Lock _lock = new();
    private readonly SessionReplayBuffer _replayBuffer = new();

    // Optimistic-concurrency counters for the compaction-rebase protocol (#532).
    // _additionVersion increments on append-only mutations (AddEntry/AddEntries).
    // _destructiveVersion increments on any mutation that removes or replaces
    // entries (ReplaceHistory, RemoveCrashSentinels with effect,
    // TryReplaceHistoryFromSnapshot with Applied/Rebased outcomes).
    // Compactors capture (snapshot, destructiveVersion, count) atomically, do
    // their slow LLM work, then re-acquire the lock to apply: if the
    // destructive version is unchanged they can either fast-path (counts
    // match) or rebase (count grew). If destructive changed they must abort.
    private long _additionVersion;
    private long _destructiveVersion;

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
            _additionVersion++;
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
            _additionVersion++;
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
            _destructiveVersion++;
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
            // Only bump the destructive version when removal actually had effect.
            // A no-op scrub must not force concurrent compactions to abort.
            var removed = Session.History.RemoveAll(static e => e.IsCrashSentinel);
            if (removed > 0)
                _destructiveVersion++;
        }
    }

    /// <summary>
    /// Atomically captures (a) an immutable snapshot of the current history,
    /// (b) the destructive-mutation version observed at snapshot time, and
    /// (c) the snapshot length. Compactors operate on the returned snapshot —
    /// not on <see cref="Session"/>.<c>History</c> — and pass the captured
    /// version+count back to <see cref="TryReplaceHistoryFromSnapshot"/> so the
    /// runtime can detect concurrent mutations and pick the safe apply path.
    /// </summary>
    public HistorySnapshot SnapshotHistoryForCompaction()
    {
        lock (_lock)
        {
            // Defensive copy: callers must not be able to mutate live history through
            // the returned list, and additions made after this call must not be
            // observable through the snapshot itself (only via Count/_additionVersion).
            var snapshot = Session.History.ToArray();
            return new HistorySnapshot(snapshot, _destructiveVersion, snapshot.Length);
        }
    }

    /// <summary>
    /// Optimistically replaces the history with <paramref name="replacement"/>,
    /// gated on the snapshot's destructive-version + count being current.
    /// See <see cref="HistoryReplaceOutcome"/> for the three possible outcomes:
    /// fast-path apply, rebased apply (concurrent additions appended), or
    /// aborted (concurrent destructive mutation detected — live history
    /// unchanged).
    /// </summary>
    public HistoryReplaceOutcome TryReplaceHistoryFromSnapshot(
        IReadOnlyList<SessionEntry> replacement,
        long expectedDestructiveVersion,
        int expectedHistoryCount)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedHistoryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedHistoryCount), "Snapshot count cannot be negative.");

        lock (_lock)
        {
            if (_destructiveVersion != expectedDestructiveVersion)
                return HistoryReplaceOutcome.Aborted;

            var currentCount = Session.History.Count;
            if (currentCount == expectedHistoryCount)
            {
                Session.History.Clear();
                Session.History.AddRange(replacement);
                _destructiveVersion++;
                Session.UpdatedAt = DateTimeOffset.UtcNow;
                return HistoryReplaceOutcome.Applied;
            }

            // Destructive version unchanged but count grew — only AddEntry/AddEntries
            // ran during the work window. Append the concurrent tail after the
            // replacement so additions are preserved.
            if (currentCount > expectedHistoryCount)
            {
                var concurrentTail = new SessionEntry[currentCount - expectedHistoryCount];
                Session.History.CopyTo(expectedHistoryCount, concurrentTail, 0, concurrentTail.Length);
                Session.History.Clear();
                Session.History.AddRange(replacement);
                Session.History.AddRange(concurrentTail);
                _destructiveVersion++;
                Session.UpdatedAt = DateTimeOffset.UtcNow;
                return HistoryReplaceOutcome.Rebased;
            }

            // currentCount < expectedHistoryCount with destructive version unchanged
            // is unreachable in current code paths (only RemoveCrashSentinels and
            // ReplaceHistory shrink, both of which bump _destructiveVersion when
            // they have effect). Treat as a conflict for safety.
            return HistoryReplaceOutcome.Aborted;
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
