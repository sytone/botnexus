using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Tests.Diagnostics;

public class ActiveLoopTrackerTests
{
    [Fact]
    public void InitialState_AllZeros()
    {
        var tracker = new ActiveLoopTracker();
        tracker.ActiveCount.ShouldBe(0);
        tracker.PeakCount.ShouldBe(0);
        tracker.TotalCompleted.ShouldBe(0);
    }

    [Fact]
    public void TrackStart_IncrementsActiveCount()
    {
        var tracker = new ActiveLoopTracker();
        tracker.TrackStart();
        tracker.ActiveCount.ShouldBe(1);
        tracker.PeakCount.ShouldBe(1);
        tracker.TotalCompleted.ShouldBe(0);
    }

    [Fact]
    public void TrackEnd_DecrementsActiveCount_IncrementsTotalCompleted()
    {
        var tracker = new ActiveLoopTracker();
        var reg = tracker.TrackStart();
        tracker.TrackEnd(reg);
        tracker.ActiveCount.ShouldBe(0);
        tracker.PeakCount.ShouldBe(1);
        tracker.TotalCompleted.ShouldBe(1);
    }

    [Fact]
    public void PeakCount_TracksHighWaterMark()
    {
        var tracker = new ActiveLoopTracker();
        var a = tracker.TrackStart();
        var b = tracker.TrackStart();
        tracker.TrackStart();
        tracker.TrackEnd(a);
        tracker.TrackEnd(b);
        tracker.ActiveCount.ShouldBe(1);
        tracker.PeakCount.ShouldBe(3);
        tracker.TotalCompleted.ShouldBe(2);
    }

    [Fact]
    public void ConcurrentAccess_MaintainsConsistency()
    {
        var tracker = new ActiveLoopTracker();
        const int iterations = 1000;

        Parallel.For(0, iterations, i =>
        {
            var reg = tracker.TrackStart($"agent-{i % 4}", $"c_{i}", $"s_{i}");
            Thread.SpinWait(10);
            tracker.TrackEnd(reg);
        });

        tracker.ActiveCount.ShouldBe(0);
        tracker.TotalCompleted.ShouldBe(iterations);
        tracker.PeakCount.ShouldBeGreaterThan(0);
        tracker.PeakCount.ShouldBeLessThanOrEqualTo(iterations);
        tracker.GetSnapshot().ActiveLoops.ShouldBeEmpty();
    }

    [Fact]
    public void PeakCount_DoesNotDecrease_AfterTrackEnd()
    {
        var tracker = new ActiveLoopTracker();
        var a = tracker.TrackStart();
        var b = tracker.TrackStart();
        var peakAfterTwo = tracker.PeakCount;
        tracker.TrackEnd(a);
        tracker.TrackEnd(b);
        tracker.PeakCount.ShouldBe(peakAfterTwo);
    }

    // ---- #2794: contextual tracking and snapshot consistency -------------------------------

    /// <summary>
    /// AC1 happy path. Named in AC7: reverting the contextual tracking (dropping agent/conversation/
    /// session/start-time retention) makes this test fail by name.
    /// </summary>
    [Fact]
    public void GetSnapshot_RetainsAgentConversationSessionAndStartTime_ForEachActiveLoop()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var tracker = new ActiveLoopTracker(time);

        tracker.TrackStart("farnsworth", "c_abc", "s_1");
        time.Advance(TimeSpan.FromSeconds(30));
        tracker.TrackStart("nova", "c_def", "s_2");

        var snapshot = tracker.GetSnapshot();

        snapshot.ActiveCount.ShouldBe(2);
        snapshot.ActiveLoops.Count.ShouldBe(2);

        var first = snapshot.ActiveLoops[0];
        first.AgentId.ShouldBe("farnsworth");
        first.ConversationId.ShouldBe("c_abc");
        first.SessionId.ShouldBe("s_1");
        first.StartedAtUtc.ShouldBe(start);
        first.LoopId.ShouldNotBeNullOrWhiteSpace();

        var second = snapshot.ActiveLoops[1];
        second.AgentId.ShouldBe("nova");
        second.ConversationId.ShouldBe("c_def");
        second.SessionId.ShouldBe("s_2");
        second.StartedAtUtc.ShouldBe(start.AddSeconds(30));

        // Distinct runs must have distinct identities even for the same agent/conversation.
        first.LoopId.ShouldNotBe(second.LoopId);
    }

    /// <summary>AC1: multiple simultaneous loops for the SAME agent stay individually addressable.</summary>
    [Fact]
    public void TrackStart_SameAgentTwice_ProducesTwoIndependentlyRemovableRuns()
    {
        var tracker = new ActiveLoopTracker();
        var first = tracker.TrackStart("farnsworth", "c_abc", "s_1");
        var second = tracker.TrackStart("farnsworth", "c_abc", "s_2");

        first.Id.ShouldNotBe(second.Id);
        tracker.ActiveCount.ShouldBe(2);

        tracker.TrackEnd(first);

        var snapshot = tracker.GetSnapshot();
        snapshot.ActiveCount.ShouldBe(1);
        snapshot.ActiveLoops.Single().SessionId.ShouldBe("s_2");
    }

    /// <summary>AC1: <c>TrackEnd</c> removes the exact run, not an arbitrary one.</summary>
    [Fact]
    public void TrackEnd_RemovesTheExactRun_LeavingOthersIntact()
    {
        var tracker = new ActiveLoopTracker();
        tracker.TrackStart("a", "c_1", "s_1");
        var middle = tracker.TrackStart("b", "c_2", "s_2");
        tracker.TrackStart("c", "c_3", "s_3");

        tracker.TrackEnd(middle);

        var loops = tracker.GetSnapshot().ActiveLoops;
        loops.Select(l => l.AgentId).ShouldBe(["a", "c"], ignoreOrder: true);
    }

    /// <summary>Sad path: a repeated or unknown registration must not corrupt the counters.</summary>
    [Fact]
    public void TrackEnd_WithUnknownOrRepeatedRegistration_IsIgnored()
    {
        var tracker = new ActiveLoopTracker();
        var reg = tracker.TrackStart("a", "c_1", "s_1");

        tracker.TrackEnd(reg);
        tracker.TrackEnd(reg);                                 // repeat
        tracker.TrackEnd(new ActiveLoopRegistration(Guid.NewGuid())); // never started
        tracker.TrackEnd(ActiveLoopRegistration.None);         // untracked sentinel

        tracker.ActiveCount.ShouldBe(0);
        tracker.TotalCompleted.ShouldBe(1);
    }

    /// <summary>Sad path: unknown context is stored as null rather than blank strings.</summary>
    [Fact]
    public void TrackStart_WithMissingContext_StoresNullsAndStillTracksTheRun()
    {
        var tracker = new ActiveLoopTracker();
        tracker.TrackStart(agentId: null, conversationId: "   ", sessionId: "");

        var loop = tracker.GetSnapshot().ActiveLoops.Single();
        loop.AgentId.ShouldBeNull();
        loop.ConversationId.ShouldBeNull();
        loop.SessionId.ShouldBeNull();
        loop.LoopId.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// AC2: the headline count and the detail list come from one materialised snapshot, so they can
    /// never disagree - asserted here under concurrent starts and completions.
    /// </summary>
    [Fact]
    public void GetSnapshot_CountAlwaysEqualsDetailListSize_UnderConcurrentStartsAndEnds()
    {
        var tracker = new ActiveLoopTracker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var churn = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                var reg = tracker.TrackStart($"agent-{i % 3}", $"c_{i}", $"s_{i}");
                Thread.SpinWait(5);
                tracker.TrackEnd(reg);
                i++;
            }
        });

        while (!cts.IsCancellationRequested)
        {
            var snapshot = tracker.GetSnapshot();
            snapshot.ActiveCount.ShouldBe(snapshot.ActiveLoops.Count);
        }

        churn.GetAwaiter().GetResult();
    }

    /// <summary>Minimal deterministic clock; avoids a dependency on the test-time package.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
