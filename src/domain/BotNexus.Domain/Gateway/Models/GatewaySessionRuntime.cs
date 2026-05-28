namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Infrastructure runtime wrapper for <see cref="Session"/> that provides thread-safe mutation
/// and replay-buffer behavior used by gateway real-time delivery paths.
/// </summary>
public sealed class GatewaySessionRuntime
{
    private readonly Lock _lock = new();

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
        StreamReplay = new SessionStreamReplay(new SessionReplayBuffer(), session);
    }

    /// <summary>
    /// Gets the session.
    /// </summary>
    public Session Session { get; }

    /// <summary>
    /// Gets the dedicated outbound-stream reconnect-replay peer for this
    /// session. The 8 forwarding stream-replay methods previously hosted here
    /// moved to <see cref="SessionStreamReplay"/> (#575); production and test
    /// callers must funnel through <c>session.StreamReplay</c>. Channels own
    /// the actual transport; this peer is only the in-memory sequenced log.
    /// </summary>
    public SessionStreamReplay StreamReplay { get; }

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
    /// Atomically appends <paramref name="entry"/> to the history AND returns
    /// an immutable snapshot of the resulting history together with the
    /// destructive-mutation version observed at snapshot time. Use this when
    /// a subsequent locked operation (such as heartbeat ack-prune) must
    /// identify the just-appended entry by its position — calling
    /// <see cref="AddEntry"/> followed by <see cref="SnapshotHistoryForCompaction"/>
    /// as separate locked operations leaves a window where a concurrent
    /// destructive mutation can shift the appended entry away from
    /// <c>snapshot.Count - 1</c>. Also captures the
    /// <see cref="Session.UpdatedAt"/> value observed immediately before the
    /// append in <see cref="SessionAppendResult.PriorUpdatedAt"/>, so callers
    /// that intend to restore UpdatedAt after a slow follow-on operation
    /// don't have to do an unsafe pre-call read.
    /// </summary>
    /// <param name="entry">The entry to append.</param>
    /// <returns>
    /// The result of the append. <see cref="SessionAppendResult.Snapshot"/>
    /// is taken under the same lock as the append (the appended entry is
    /// always at index <c>snapshot.Count - 1</c>);
    /// <see cref="SessionAppendResult.PriorUpdatedAt"/> is the
    /// <see cref="Session.UpdatedAt"/> value sampled immediately before the
    /// append, also under the same lock.
    /// </returns>
    public SessionAppendResult AddEntryAndSnapshot(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            // Capture the pre-append UpdatedAt under the same lock as the
            // append (rubber-duck bug-hunt BLOCKING for #573). A callsite
            // that reads session.UpdatedAt OUTSIDE the lock before calling
            // AddEntryAndSnapshot can be raced by a concurrent destructive
            // mutation that lands between the read and the lock, leaving
            // the caller with a stale anchor that — if later passed to
            // TryReplaceHistoryFromSnapshot(restoreUpdatedAtOnApplied: ...)
            // on the Applied path — would roll the timestamp backwards
            // past the concurrent mutation.
            var priorUpdatedAt = Session.UpdatedAt;
            Session.History.Add(entry);
            _additionVersion++;
            Session.UpdatedAt = DateTimeOffset.UtcNow;
            var snapshot = Session.History.ToArray();
            return new SessionAppendResult(
                new HistorySnapshot(snapshot, _destructiveVersion, snapshot.Length),
                priorUpdatedAt);
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
    /// <param name="replacement">The replacement history derived from the snapshot.</param>
    /// <param name="expectedDestructiveVersion">Destructive-mutation version observed when the snapshot was taken.</param>
    /// <param name="expectedHistoryCount">Snapshot count.</param>
    /// <param name="restoreUpdatedAtOnApplied">
    /// When non-null AND the apply takes the fast Applied path (no concurrent
    /// activity), the session's <c>UpdatedAt</c> is restored to this value
    /// INSIDE the same runtime lock as the apply. The lock is the critical
    /// invariant: a separate post-apply write to <c>UpdatedAt</c> would race
    /// with concurrent <see cref="AddEntry"/> calls and clobber legitimate
    /// fresh activity timestamps. On Rebased the parameter is ignored (the
    /// concurrent activity legitimately bumped <c>UpdatedAt</c>); on Aborted
    /// no write occurs.
    /// </param>
    public HistoryReplaceOutcome TryReplaceHistoryFromSnapshot(
        IReadOnlyList<SessionEntry> replacement,
        long expectedDestructiveVersion,
        int expectedHistoryCount,
        DateTimeOffset? restoreUpdatedAtOnApplied = null)
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
                // Restore caller-supplied UpdatedAt under the lock when supplied;
                // otherwise stamp the apply time. The lock is the invariant —
                // a separate post-apply write would race with concurrent AddEntry.
                Session.UpdatedAt = restoreUpdatedAtOnApplied ?? DateTimeOffset.UtcNow;
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
                // Rebased: concurrent activity is real; do NOT honor
                // restoreUpdatedAtOnApplied — restoring to a stale pre-apply
                // value would lie about activity timing.
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

}
