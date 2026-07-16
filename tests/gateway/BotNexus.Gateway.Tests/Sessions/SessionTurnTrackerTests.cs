using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="SessionTurnTracker"/> - the reference-counted live-turn tracker
/// that powers write-time crash-sentinel self-heal (#2030).
/// </summary>
public sealed class SessionTurnTrackerTests
{
    [Fact]
    public void HasLiveTurn_UnknownSession_ReturnsFalse()
    {
        var tracker = new SessionTurnTracker();
        tracker.HasLiveTurn("nope").ShouldBeFalse();
    }

    [Fact]
    public void BeginTurn_MarksSessionLive_UntilScopeDisposed()
    {
        var tracker = new SessionTurnTracker();
        var scope = tracker.BeginTurn("s1");
        tracker.HasLiveTurn("s1").ShouldBeTrue();

        scope.Dispose();
        tracker.HasLiveTurn("s1").ShouldBeFalse();
    }

    [Fact]
    public void BeginTurn_IsReferenceCounted_StaysLiveUntilAllScopesDisposed()
    {
        var tracker = new SessionTurnTracker();
        var a = tracker.BeginTurn("s1");
        var b = tracker.BeginTurn("s1");

        tracker.HasLiveTurn("s1").ShouldBeTrue();
        a.Dispose();
        tracker.HasLiveTurn("s1").ShouldBeTrue("session must stay live while a nested scope remains open");
        b.Dispose();
        tracker.HasLiveTurn("s1").ShouldBeFalse();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var tracker = new SessionTurnTracker();
        var a = tracker.BeginTurn("s1");
        var b = tracker.BeginTurn("s1");

        a.Dispose();
        a.Dispose(); // double-dispose must not decrement twice
        tracker.HasLiveTurn("s1").ShouldBeTrue("double-dispose must not over-decrement the counter");

        b.Dispose();
        tracker.HasLiveTurn("s1").ShouldBeFalse();
    }

    [Fact]
    public void Tracker_IsolatesSessions()
    {
        var tracker = new SessionTurnTracker();
        using var _ = tracker.BeginTurn("s1");

        tracker.HasLiveTurn("s1").ShouldBeTrue();
        tracker.HasLiveTurn("s2").ShouldBeFalse();
    }
}
