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
        tracker.TrackStart();

        tracker.TrackEnd();

        tracker.ActiveCount.ShouldBe(0);
        tracker.PeakCount.ShouldBe(1);
        tracker.TotalCompleted.ShouldBe(1);
    }

    [Fact]
    public void PeakCount_TracksHighWaterMark()
    {
        var tracker = new ActiveLoopTracker();

        tracker.TrackStart();
        tracker.TrackStart();
        tracker.TrackStart();
        tracker.TrackEnd();
        tracker.TrackEnd();

        tracker.ActiveCount.ShouldBe(1);
        tracker.PeakCount.ShouldBe(3);
        tracker.TotalCompleted.ShouldBe(2);
    }

    [Fact]
    public void ConcurrentAccess_MaintainsConsistency()
    {
        var tracker = new ActiveLoopTracker();
        const int iterations = 1000;

        Parallel.For(0, iterations, _ =>
        {
            tracker.TrackStart();
            Thread.SpinWait(10);
            tracker.TrackEnd();
        });

        tracker.ActiveCount.ShouldBe(0);
        tracker.TotalCompleted.ShouldBe(iterations);
        tracker.PeakCount.ShouldBeGreaterThan(0);
        tracker.PeakCount.ShouldBeLessThanOrEqualTo(iterations);
    }

    [Fact]
    public void PeakCount_DoesNotDecrease_AfterTrackEnd()
    {
        var tracker = new ActiveLoopTracker();

        tracker.TrackStart();
        tracker.TrackStart();
        var peakAfterTwo = tracker.PeakCount;
        tracker.TrackEnd();
        tracker.TrackEnd();

        tracker.PeakCount.ShouldBe(peakAfterTwo);
    }
}
