using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Satellites;
using BotNexus.Gateway.Satellites;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Satellites;

/// <summary>
/// Freshness must survive a host clock correction (#3780). These tests drive the wall clock and the
/// monotonic clock independently, which is the only way to distinguish "the registry reads one clock"
/// from "the registry happens to agree with itself while nothing moves".
/// </summary>
public sealed class InMemorySatelliteRegistryClockStepTests
{
    /// <summary>
    /// A clock whose wall-clock reading can be stepped in either direction while its monotonic
    /// timestamp only ever advances - exactly the shape of an NTP step, a VM resume, or a container
    /// host time sync. A registry that mixes the two is observable here and nowhere else.
    /// </summary>
    private sealed class SteppableClock : TimeProvider
    {
        private long _wallTicks;
        private long _stamp;

        public SteppableClock(DateTimeOffset start)
        {
            _wallTicks = start.UtcTicks;
            _stamp = 1_000_000;
        }

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _wallTicks), TimeSpan.Zero);

        public override long GetTimestamp() => Interlocked.Read(ref _stamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Advances real elapsed time: both clocks move forward together.</summary>
        public void AdvanceMonotonic(TimeSpan duration)
        {
            Interlocked.Add(ref _stamp, duration.Ticks);
            Interlocked.Add(ref _wallTicks, duration.Ticks);
        }

        /// <summary>Steps ONLY the wall clock, leaving elapsed time untouched.</summary>
        public void StepWallClock(TimeSpan delta) => Interlocked.Add(ref _wallTicks, delta.Ticks);
    }

    private static SatelliteConnectionInfo Entry(string id, int staleTimeoutSeconds, DateTimeOffset lastSeen) => new()
    {
        Id = id,
        DisplayName = id,
        Platform = "windows",
        OwnerUserId = "jon",
        Status = SatelliteStatus.Online,
        LastSeen = lastSeen,
        StaleTimeoutSeconds = staleTimeoutSeconds
    };

    [Fact]
    public void GetStaleSatellites_WallClockSteppedBackwards_StoppedSatelliteStillStale()
    {
        var start = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        var clock = new SteppableClock(start);
        var registry = new InMemorySatelliteRegistry(
            [Entry("dead-sat", staleTimeoutSeconds: 60, lastSeen: start)],
            NullLogger<InMemorySatelliteRegistry>.Instance,
            clock);

        // The satellite stops heartbeating for well over its timeout...
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(300));

        // ...and the host clock is corrected backwards by more than the timeout. Under the old
        // mixed-clock comparison (now - LastSeen) this went negative and pinned the satellite Online.
        clock.StepWallClock(TimeSpan.FromSeconds(-600));

        var stale = registry.GetStaleSatellites();

        Assert.Single(stale);
        Assert.Equal("dead-sat", stale[0].Id);
    }

    [Fact]
    public void GetStaleSatellites_WallClockSteppedForwards_LiveSatelliteNotStale()
    {
        var start = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        var clock = new SteppableClock(start);
        var registry = new InMemorySatelliteRegistry(
            [Entry("live-sat", staleTimeoutSeconds: 60, lastSeen: start)],
            NullLogger<InMemorySatelliteRegistry>.Instance,
            clock);

        // One monotonic second of real elapsed time since the heartbeat.
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(1));

        // A forward host clock correction far exceeding the timeout must not expire the fleet.
        clock.StepWallClock(TimeSpan.FromSeconds(600));

        Assert.Empty(registry.GetStaleSatellites());
    }

    [Fact]
    public void LastSeen_RemainsWallClockValue_ForDisplay()
    {
        var start = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        var clock = new SteppableClock(start);
        var registry = new InMemorySatelliteRegistry(
            [Entry("sat1", staleTimeoutSeconds: 60, lastSeen: start)],
            NullLogger<InMemorySatelliteRegistry>.Instance,
            clock);

        registry.MarkOnline("sat1", "conn-1");
        Assert.Equal(start, registry.GetById("sat1")!.LastSeen);

        clock.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        registry.RecordHeartbeat("sat1");

        Assert.Equal(start.AddSeconds(30), registry.GetById("sat1")!.LastSeen);
    }
}
