using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

public sealed class GatewaySessionThreadSafetyTests
{
    [Fact]
    public async Task AddEntry_WithConcurrentWriters_DoesNotCorruptHistory()
    {
        var session = CreateSession();
        const int totalEntries = 500;

        var writers = Enumerable.Range(0, totalEntries)
            .Select(i => Task.Run(() => session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"entry-{i}" })));
        await Task.WhenAll(writers);

        var snapshot = session.GetHistorySnapshot();
        snapshot.Count().ShouldBe(totalEntries);
        snapshot.Select(e => e.Content).ShouldBeUnique();
    }

    [Fact]
    public async Task GetHistorySnapshot_DuringConcurrentMutation_ReturnsConsistentSnapshots()
    {
        var session = CreateSession();
        var cts = new CancellationTokenSource();
        var errors = new ConcurrentQueue<Exception>();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 250; i++)
            {
                session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = $"m-{i}" });
                await Task.Yield();
            }

            cts.Cancel();
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var snapshot = session.GetHistorySnapshot();
                    snapshot.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.Role));
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }
        });

        await Task.WhenAll(writer, reader);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddEntries_WhenObservedConcurrently_IsAtomicPerBatch()
    {
        var session = CreateSession();
        const int batchSize = 4;
        const int batchCount = 100;
        var cts = new CancellationTokenSource();
        var errors = new ConcurrentQueue<string>();

        var writer = Task.Run(async () =>
        {
            for (var batch = 0; batch < batchCount; batch++)
            {
                var batchEntries = Enumerable.Range(0, batchSize)
                    .Select(i => new SessionEntry { Role = MessageRole.User, Content = $"batch-{batch}-entry-{i}" });
                session.AddEntries(batchEntries);
                await Task.Yield();
            }

            cts.Cancel();
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = session.GetHistorySnapshot();
                var grouped = snapshot
                    .GroupBy(e =>
                    {
                        var markerIndex = e.Content.LastIndexOf("-entry-", StringComparison.Ordinal);
                        return markerIndex >= 0 ? e.Content[..markerIndex] : e.Content;
                    });

                foreach (var group in grouped)
                {
                    if (group.Count() != batchSize)
                    {
                        errors.Enqueue($"Batch '{group.Key}' had non-atomic visibility with {group.Count()} entries.");
                    }
                }
            }
        });

        await Task.WhenAll(writer, reader);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void GetHistorySnapshot_WithPagination_ReturnsRequestedSegment()
    {
        var session = CreateSession();
        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"entry-{i}" });

        var snapshot = session.GetHistorySnapshot(offset: 3, limit: 4);

        snapshot.Count().ShouldBe(4);
        snapshot[0].Content.ShouldBe("entry-3");
        snapshot[^1].Content.ShouldBe("entry-6");
    }

    [Fact]
    public void AllocateSequenceId_WhenCalledSequentially_IncrementsMonotonically()
    {
        var session = CreateSession();

        var first = session.StreamReplay.AllocateSequenceId();
        var second = session.StreamReplay.AllocateSequenceId();
        var third = session.StreamReplay.AllocateSequenceId();

        first.ShouldBe(1);
        second.ShouldBe(2);
        third.ShouldBe(3);
    }

    [Fact]
    public void AddEvent_WithReplayWindow_KeepsLatestBoundedEvents()
    {
        var session = CreateSession();

        session.StreamReplay.AddEvent(1, """{"type":"connected","sequenceId":1}""", replayWindowSize: 2);
        session.StreamReplay.AddEvent(2, """{"type":"pong","sequenceId":2}""", replayWindowSize: 2);
        session.StreamReplay.AddEvent(3, """{"type":"pong","sequenceId":3}""", replayWindowSize: 2);

        var replay = session.StreamReplay.GetEventSnapshot();
        replay.Count().ShouldBe(2);
        replay.Select(evt => evt.SequenceId).ToList().ShouldBe(new long[] { 2, 3 });
    }

    [Fact]
    public void GetEventsAfter_WithNoMissedMessages_ReturnsEmpty()
    {
        var session = CreateSession();
        session.StreamReplay.AddEvent(1, """{"type":"connected","sequenceId":1}""", replayWindowSize: 10);
        session.StreamReplay.AddEvent(2, """{"type":"pong","sequenceId":2}""", replayWindowSize: 10);

        var replay = session.StreamReplay.GetEventsAfter(lastSequenceId: 2, maxReplayCount: 10);

        replay.ShouldBeEmpty();
    }

    [Fact]
    public void StreamReplay_AccessedViaFacade_DeliversSameSnapshotAndAfterQuery()
    {
        // Pre-#575 this test reached through `session.ReplayBuffer.AddStreamEvent(...)`,
        // bypassing the UpdatedAt-stamping facade. The `session.ReplayBuffer`
        // accessor no longer exists and the `SessionStreamReplayArchitectureTests`
        // fence prevents the reach-through shape from returning; this test now
        // exercises the new facade and pins the snapshot/after-query round-trip.
        var session = CreateSession();

        session.StreamReplay.AddEvent(1, """{"type":"connected","sequenceId":1}""", replayWindowSize: 2);
        session.StreamReplay.AddEvent(2, """{"type":"pong","sequenceId":2}""", replayWindowSize: 2);
        session.StreamReplay.AddEvent(3, """{"type":"pong","sequenceId":3}""", replayWindowSize: 2);

        session.StreamReplay.GetEventSnapshot().Select(evt => evt.SequenceId).ToList().ShouldBe(new long[] { 2, 3 });
        session.StreamReplay.GetEventsAfter(lastSequenceId: 2, maxReplayCount: 10)
            .Select(evt => evt.SequenceId)
            .ShouldHaveSingleItem()
            .ShouldBe(3);
    }

    [Fact]
    public void AddEntryAndSnapshot_AppendsEntry_AndReturnsSnapshotWithEntryAsLast()
    {
        var session = CreateSession();
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u1" });

        var appendResult = session.AddEntryAndSnapshot(new SessionEntry { Role = MessageRole.User, Content = "hb" });

        appendResult.Snapshot.Count.ShouldBe(2);
        appendResult.Snapshot.Entries[^1].Content.ShouldBe("hb");
        session.History.Count.ShouldBe(2);
        session.History[^1].Content.ShouldBe("hb");
    }

    [Fact]
    public void AddEntryAndSnapshot_CapturesPriorUpdatedAt_AtomicallyWithAppend()
    {
        // The atomicity contract: PriorUpdatedAt reflects what UpdatedAt
        // was IMMEDIATELY BEFORE the append (under the same lock), not
        // what a caller's unlocked pre-call read might have observed.
        // Without this contract, a caller pattern like
        //     pre = session.UpdatedAt;          // UNLOCKED read
        //     snap = session.AddEntryAndSnapshot(...);
        // is race-prone — a concurrent ReplaceHistory can land between the
        // read and the lock, leaving `pre` stale. This pin makes the
        // atomic-prior-UpdatedAt the contract.
        var session = CreateSession();
        var oldAnchor = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u1" });
        session.UpdatedAt = oldAnchor;

        var appendResult = session.AddEntryAndSnapshot(new SessionEntry { Role = MessageRole.User, Content = "hb" });

        appendResult.PriorUpdatedAt.ShouldBe(oldAnchor);
        // The append itself stamped UpdatedAt forward; the returned
        // PriorUpdatedAt is the pre-append observation, not the post.
        session.UpdatedAt.ShouldNotBe(oldAnchor);
    }

    [Fact]
    public async Task AddEntryAndSnapshot_UnderConcurrentReplaceHistory_PriorUpdatedAtNeverPredatesObservedDestructiveStamp()
    {
        // Rubber-duck bug-hunt BLOCKING regression (#573): without atomic
        // capture, a caller that reads UpdatedAt before AddEntryAndSnapshot
        // could observe a stale pre-ReplaceHistory time and then restore
        // backwards on Applied. Atomic capture closes that window: every
        // AddEntryAndSnapshot's PriorUpdatedAt is consistent with the
        // history state that ReplaceHistory left behind.
        var session = CreateSession();
        const int iterations = 200;
        var iterationCounter = 0;
        var violations = 0;

        var appender = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var observedBefore = session.UpdatedAt;
                var appendResult = session.AddEntryAndSnapshot(new SessionEntry
                {
                    Role = MessageRole.User,
                    Content = $"hb-{i}"
                });
                // PriorUpdatedAt cannot predate any prior mutation that
                // the appender's own pre-call read observed (because the
                // atomic lock pairs prior-UpdatedAt with the append).
                if (appendResult.PriorUpdatedAt < observedBefore)
                    Interlocked.Increment(ref violations);
                Interlocked.Increment(ref iterationCounter);
            }
        });

        var destroyer = Task.Run(() =>
        {
            while (Volatile.Read(ref iterationCounter) < iterations)
            {
                session.ReplaceHistory([new SessionEntry { Role = MessageRole.System, Content = "wipe" }]);
            }
        });

        await Task.WhenAll(appender, destroyer);
        violations.ShouldBe(0);
    }

    [Fact]
    public async Task AddEntryAndSnapshot_UnderConcurrentReplaceHistory_AppendIsAtomicWithSnapshot()
    {
        // The atomicity invariant the heartbeat path depends on: across
        // many interleavings of AddEntryAndSnapshot vs ReplaceHistory, the
        // returned snapshot must ALWAYS identify the appended entry as the
        // last one — never have the destructive mutation shift it out.
        // (A naive AddEntry-then-Snapshot pair would fail this contract
        // because the destructive mutation can land between the two calls.)
        var session = CreateSession();
        const int iterations = 200;
        var mismatches = 0;
        var iterationCounter = 0;

        var appender = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var appendResult = session.AddEntryAndSnapshot(new SessionEntry
                {
                    Role = MessageRole.User,
                    Content = $"hb-{i}"
                });
                if (appendResult.Snapshot.Entries[^1].Content != $"hb-{i}")
                    Interlocked.Increment(ref mismatches);
                Interlocked.Increment(ref iterationCounter);
            }
        });

        var destroyer = Task.Run(() =>
        {
            while (Volatile.Read(ref iterationCounter) < iterations)
            {
                session.ReplaceHistory([new SessionEntry { Role = MessageRole.System, Content = "wipe" }]);
            }
        });

        await Task.WhenAll(appender, destroyer);
        mismatches.ShouldBe(0);
    }

    [Fact]
    public void TryReplaceHistoryFromSnapshot_AppliedPath_RestoresUpdatedAtFromCaller()
    {
        var session = CreateSession();
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u1" });
        var appendResult = session.AddEntryAndSnapshot(new SessionEntry { Role = MessageRole.User, Content = "hb" });
        var restoreTo = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var outcome = session.TryReplaceHistoryFromSnapshot(
            [new SessionEntry { Role = MessageRole.User, Content = "u1" }],
            appendResult.Snapshot.DestructiveVersion,
            appendResult.Snapshot.Count,
            restoreUpdatedAtOnApplied: restoreTo);

        outcome.ShouldBe(HistoryReplaceOutcome.Applied);
        session.UpdatedAt.ShouldBe(restoreTo);
        session.History.Select(e => e.Content).ToList().ShouldBe(["u1"]);
    }

    [Fact]
    public void TryReplaceHistoryFromSnapshot_RebasedPath_IgnoresRestoreUpdatedAt()
    {
        var session = CreateSession();
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u1" });
        var appendResult = session.AddEntryAndSnapshot(new SessionEntry { Role = MessageRole.User, Content = "hb" });
        var staleAnchor = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Concurrent AddEntry between snapshot and apply -> Rebased.
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "raced-in" });
        var preApply = session.UpdatedAt;

        var outcome = session.TryReplaceHistoryFromSnapshot(
            [new SessionEntry { Role = MessageRole.User, Content = "u1" }],
            appendResult.Snapshot.DestructiveVersion,
            appendResult.Snapshot.Count,
            restoreUpdatedAtOnApplied: staleAnchor);

        outcome.ShouldBe(HistoryReplaceOutcome.Rebased);
        session.UpdatedAt.ShouldBeGreaterThanOrEqualTo(preApply);
        session.UpdatedAt.ShouldNotBe(staleAnchor);
        session.History.Select(e => e.Content).ToList().ShouldBe(["u1", "raced-in"]);
    }

    [Fact]
    public void TryReplaceHistoryFromSnapshot_AbortedPath_DoesNotTouchUpdatedAt()
    {
        var session = CreateSession();
        var appendResult = session.AddEntryAndSnapshot(new SessionEntry { Role = MessageRole.User, Content = "hb" });

        // Concurrent destructive mutation -> Aborted.
        session.ReplaceHistory([new SessionEntry { Role = MessageRole.System, Content = "wipe" }]);
        var postWipeUpdatedAt = session.UpdatedAt;
        var anyRestoreAnchor = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var outcome = session.TryReplaceHistoryFromSnapshot(
            [],
            appendResult.Snapshot.DestructiveVersion,
            appendResult.Snapshot.Count,
            restoreUpdatedAtOnApplied: anyRestoreAnchor);

        outcome.ShouldBe(HistoryReplaceOutcome.Aborted);
        // Aborted MUST NOT touch UpdatedAt — the concurrent destructive
        // mutation already stamped it; we must not overwrite with the
        // caller's restore anchor (or anything else).
        session.UpdatedAt.ShouldBe(postWipeUpdatedAt);
        session.History.Select(e => e.Content).ToList().ShouldBe(["wipe"]);
    }

    private static GatewaySession CreateSession()
        => new() { SessionId = SessionId.From($"session-{Guid.NewGuid():N}"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
}

