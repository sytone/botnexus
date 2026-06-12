using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentExchangeBudgetTrackerTests
{
    private static AgentExchangeBudgetTracker CreateTracker(
        AgentExchangeBudgetOptions? options = null,
        TestTimeProvider? timeProvider = null)
    {
        return new AgentExchangeBudgetTracker(
            Options.Create(options ?? new AgentExchangeBudgetOptions()),
            NullLogger<AgentExchangeBudgetTracker>.Instance,
            timeProvider);
    }

    [Fact]
    public void EnsureWithinBudget_FirstCall_DoesNotThrow()
    {
        var tracker = CreateTracker();
        tracker.EnsureWithinBudget(AgentId.From("agent-a"), AgentId.From("agent-b"));
    }

    [Fact]
    public void EnsureWithinBudget_DailyCapExhausted_Throws()
    {
        var tracker = CreateTracker(new AgentExchangeBudgetOptions { DailyTurnCap = 5 });
        var a = AgentId.From("agent-a");
        var b = AgentId.From("agent-b");

        tracker.RecordExchangeComplete(a, b, 5);

        var ex = Should.Throw<InvalidOperationException>(() => tracker.EnsureWithinBudget(a, b));
        ex.Message.ShouldContain("Daily conversation budget exhausted");
    }

    [Fact]
    public void EnsureWithinBudget_DailyCapResetsNextDay()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.Zero));
        var tracker = CreateTracker(new AgentExchangeBudgetOptions { DailyTurnCap = 5 }, time);
        var a = AgentId.From("agent-a");
        var b = AgentId.From("agent-b");

        tracker.RecordExchangeComplete(a, b, 5);
        time.Advance(TimeSpan.FromHours(24));

        tracker.EnsureWithinBudget(a, b); // new day — should not throw
    }

    [Fact]
    public void EnsureWithinBudget_LoopDetected_EnforcesCooldown()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.Zero));
        var tracker = CreateTracker(
            new AgentExchangeBudgetOptions
            {
                LoopDetectionWindowSeconds = 60,
                LoopThreshold = 3,
                CooldownOnLoopDetectSeconds = 300,
                DailyTurnCap = 1000
            }, time);
        var a = AgentId.From("agent-a");
        var b = AgentId.From("agent-b");

        for (int i = 0; i < 3; i++)
        {
            tracker.EnsureWithinBudget(a, b);
            tracker.RecordExchangeComplete(a, b, 1);
            time.Advance(TimeSpan.FromSeconds(5));
        }

        // 4th triggers loop detection (counter > threshold)
        var ex = Should.Throw<InvalidOperationException>(() => tracker.EnsureWithinBudget(a, b));
        ex.Message.ShouldContain("Loop detected");
    }

    [Fact]
    public void EnsureWithinBudget_CooldownExpires_Resumes()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.Zero));
        var tracker = CreateTracker(
            new AgentExchangeBudgetOptions
            {
                LoopDetectionWindowSeconds = 60,
                LoopThreshold = 2,
                CooldownOnLoopDetectSeconds = 300,
                DailyTurnCap = 1000
            }, time);
        var a = AgentId.From("agent-a");
        var b = AgentId.From("agent-b");

        // Trigger cooldown: 2 within-window re-engagements triggers at threshold
        tracker.EnsureWithinBudget(a, b);
        tracker.RecordExchangeComplete(a, b, 1);
        time.Advance(TimeSpan.FromSeconds(5));
        tracker.EnsureWithinBudget(a, b); // counter -> 1
        tracker.RecordExchangeComplete(a, b, 1);
        time.Advance(TimeSpan.FromSeconds(5));
        // 3rd call: counter -> 2 >= threshold(2), triggers cooldown
        Should.Throw<InvalidOperationException>(() => tracker.EnsureWithinBudget(a, b));

        // After cooldown
        time.Advance(TimeSpan.FromSeconds(300));
        tracker.EnsureWithinBudget(a, b); // should not throw
    }

    [Fact]
    public void EnsureWithinBudget_OutsideWindow_ResetsLoopCounter()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.Zero));
        var tracker = CreateTracker(
            new AgentExchangeBudgetOptions
            {
                LoopDetectionWindowSeconds = 60,
                LoopThreshold = 3,
                CooldownOnLoopDetectSeconds = 300,
                DailyTurnCap = 1000
            }, time);
        var a = AgentId.From("agent-a");
        var b = AgentId.From("agent-b");

        tracker.EnsureWithinBudget(a, b);
        tracker.RecordExchangeComplete(a, b, 1);
        time.Advance(TimeSpan.FromSeconds(5));
        tracker.EnsureWithinBudget(a, b);
        tracker.RecordExchangeComplete(a, b, 1);

        // Wait beyond window
        time.Advance(TimeSpan.FromSeconds(120));
        tracker.EnsureWithinBudget(a, b); // resets counter, no exception
    }

    [Fact]
    public void DifferentPairs_IndependentBudgets()
    {
        var tracker = CreateTracker(new AgentExchangeBudgetOptions { DailyTurnCap = 5 });
        var a = AgentId.From("agent-a");
        var b = AgentId.From("agent-b");
        var c = AgentId.From("agent-c");

        tracker.RecordExchangeComplete(a, b, 5);
        Should.Throw<InvalidOperationException>(() => tracker.EnsureWithinBudget(a, b));
        tracker.EnsureWithinBudget(a, c); // independent pair — should not throw
    }

    [Fact]
    public void GetPairInfo_ReturnsNull_ForUnknownPair()
    {
        var tracker = CreateTracker();
        tracker.GetPairInfo(AgentId.From("x"), AgentId.From("y")).ShouldBeNull();
    }

    [Fact]
    public void GetPairInfo_ReturnsState_AfterExchange()
    {
        var tracker = CreateTracker(new AgentExchangeBudgetOptions { DailyTurnCap = 200 });
        var a = AgentId.From("agent-a");
        var b = AgentId.From("agent-b");

        tracker.RecordExchangeComplete(a, b, 7);

        var info = tracker.GetPairInfo(a, b);
        info.ShouldNotBeNull();
        info.DailyTurnsUsed.ShouldBe(7);
        info.DailyTurnCap.ShouldBe(200);
    }

    [Fact]
    public void GetAllPairInfo_ReturnsAllTracked()
    {
        var tracker = CreateTracker();
        tracker.RecordExchangeComplete(AgentId.From("a"), AgentId.From("b"), 3);
        tracker.RecordExchangeComplete(AgentId.From("a"), AgentId.From("c"), 5);

        var all = tracker.GetAllPairInfo();
        all.Count.ShouldBe(2);
    }
}

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset startTime)
    {
        _utcNow = startTime;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration)
    {
        _utcNow = _utcNow.Add(duration);
    }
}