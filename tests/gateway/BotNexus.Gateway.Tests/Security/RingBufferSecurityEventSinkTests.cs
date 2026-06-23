using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Tests for <see cref="RingBufferSecurityEventSink"/> -- the bounded, thread-safe,
/// trusted-only in-memory sink (Step 1/5 of the security-event taxonomy, issue #1532 / #1526).
/// </summary>
public sealed class RingBufferSecurityEventSinkTests
{
    private static SecurityEvent Event(string action, SecurityEventSeverity severity = SecurityEventSeverity.Info) =>
        new(SecurityEventCategory.Audit, action, SecurityEventOutcome.Success, severity);

    [Fact]
    public void Record_ThenSnapshot_RoundTripsTheEvent()
    {
        var sink = new RingBufferSecurityEventSink(capacity: 8);
        var evt = Event("audit.test.one");

        sink.Record(evt);

        var snapshot = sink.Snapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].ShouldBe(evt);
        sink.Count.ShouldBe(1);
    }

    [Fact]
    public void Snapshot_ReturnsMostRecentFirst()
    {
        var sink = new RingBufferSecurityEventSink(capacity: 8);
        sink.Record(Event("first"));
        sink.Record(Event("second"));
        sink.Record(Event("third"));

        var snapshot = sink.Snapshot();

        snapshot.Count.ShouldBe(3);
        snapshot[0].Action.ShouldBe("third");
        snapshot[1].Action.ShouldBe("second");
        snapshot[2].Action.ShouldBe("first");
    }

    [Fact]
    public void Record_PastCapacity_EvictsOldestFirst()
    {
        var sink = new RingBufferSecurityEventSink(capacity: 3);

        sink.Record(Event("e1"));
        sink.Record(Event("e2"));
        sink.Record(Event("e3"));
        sink.Record(Event("e4")); // evicts e1
        sink.Record(Event("e5")); // evicts e2

        sink.Count.ShouldBe(3);
        var actions = sink.Snapshot().Select(e => e.Action).ToList();
        actions.ShouldBe(new[] { "e5", "e4", "e3" });
        actions.ShouldNotContain("e1");
        actions.ShouldNotContain("e2");
    }

    [Fact]
    public void Count_NeverExceedsCapacity()
    {
        var sink = new RingBufferSecurityEventSink(capacity: 5);
        for (var i = 0; i < 50; i++)
            sink.Record(Event($"e{i}"));

        sink.Count.ShouldBe(5);
        sink.Snapshot().Count.ShouldBe(5);
        // The five most recent survive, most-recent-first.
        sink.Snapshot().Select(e => e.Action).ShouldBe(new[] { "e49", "e48", "e47", "e46", "e45" });
    }

    [Fact]
    public void Clear_EmptiesTheBuffer()
    {
        var sink = new RingBufferSecurityEventSink(capacity: 4);
        sink.Record(Event("a"));
        sink.Record(Event("b"));

        sink.Clear();

        sink.Count.ShouldBe(0);
        sink.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RingBufferSecurityEventSink(capacity: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new RingBufferSecurityEventSink(capacity: -1));
    }

    [Fact]
    public void Record_RejectsNullEvent()
    {
        var sink = new RingBufferSecurityEventSink(capacity: 4);
        Should.Throw<ArgumentNullException>(() => sink.Record(null!));
    }

    [Fact]
    public async Task ConcurrentWrites_DoNotCorruptOrExceedCapacity()
    {
        const int capacity = 64;
        const int writers = 16;
        const int perWriter = 500;
        var sink = new RingBufferSecurityEventSink(capacity);

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
                sink.Record(Event($"w{w}-i{i}"));
        }));

        await Task.WhenAll(tasks);

        // Buffer must be exactly full and internally consistent (no torn entries).
        sink.Count.ShouldBe(capacity);
        var snapshot = sink.Snapshot();
        snapshot.Count.ShouldBe(capacity);
        snapshot.ShouldAllBe(e => e != null);
        // Total writes far exceeded capacity, so the buffer is saturated with valid events.
        snapshot.Select(e => e.Action).Distinct().Count().ShouldBe(capacity);
    }

    [Fact]
    public void ImplementsISecurityEventSink()
    {
        ISecurityEventSink sink = new RingBufferSecurityEventSink(capacity: 4);
        sink.Record(Event("via-interface"));
        sink.Snapshot()[0].Action.ShouldBe("via-interface");
    }
}
