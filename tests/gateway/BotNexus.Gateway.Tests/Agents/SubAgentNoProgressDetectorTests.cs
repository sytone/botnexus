using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentNoProgressDetectorTests
{
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(1000, 64)]
    public void Observe_InvalidOptions_ClampsWindowAndThreshold(int configured, int expected)
    {
        var detector = new SubAgentNoProgressDetector(configured, configured);
        for (var turn = 1; turn <= expected; turn++)
        {
            detector.Observe(new(AgentActivityKind.ToolFailed, 1, 0)).ShouldBeNull();
            var decision = detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0));
            if (turn < expected) decision.ShouldBeNull();
            else decision.ShouldNotBeNull().MatchedTurns.ShouldBe(expected);
        }
    }

    [Fact]
    public void Observe_NewSuccessLaterInFailingBatch_InvalidatesPendingStop()
    {
        var detector = new SubAgentNoProgressDetector(3, 3);
        for (var turn = 0; turn < 3; turn++)
        {
            detector.Observe(new(AgentActivityKind.ToolFailed, 1, 0)).ShouldBeNull();
            if (turn == 2) detector.Observe(new(AgentActivityKind.ToolSucceeded, 2, 0)).ShouldBeNull();
            detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(AgentActivityKind.ToolSucceeded)]
    [InlineData(AgentActivityKind.ToolFailed)]
    public void Observe_IdenticalProgressUnknownEveryTurn_ResetsWindowWithoutDeduplication(AgentActivityKind repeatedKind)
    {
        var detector = new SubAgentNoProgressDetector(3, 3);
        var repeated = new AgentActivityObservation(repeatedKind, 1, 0);
        var unknown = new AgentActivityObservation(AgentActivityKind.ToolProgressUnknown, 2, 0);
        // Prime successful evidence so it is genuinely unchanged in subsequent turns.
        detector.Observe(repeated).ShouldBeNull();
        detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();

        for (var turn = 0; turn < 12; turn++)
        {
            detector.Observe(repeated).ShouldBeNull();
            detector.Observe(unknown).ShouldBeNull();
            detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
            detector.RetainedTurnCount.ShouldBe(0);
            detector.PendingToolCount.ShouldBe(0);
            detector.RetainedSuccessCount.ShouldBe(repeatedKind == AgentActivityKind.ToolSucceeded ? 1 : 0);
        }
    }

    [Theory]
    [InlineData(AgentActivityKind.ToolSucceeded, false)]
    [InlineData(AgentActivityKind.ToolSucceeded, true)]
    [InlineData(AgentActivityKind.ToolFailed, false)]
    [InlineData(AgentActivityKind.ToolFailed, true)]
    public void Observe_ProgressUnknownInThresholdBatch_ClearsHistoryAndRequiresFreshWindow(
        AgentActivityKind repeatedKind, bool unknownFirst)
    {
        var detector = new SubAgentNoProgressDetector(3, 3);
        var repeated = new AgentActivityObservation(repeatedKind, 1, 0);
        var unknown = new AgentActivityObservation(AgentActivityKind.ToolProgressUnknown, 2, 0);
        if (repeatedKind == AgentActivityKind.ToolSucceeded)
        {
            detector.Observe(repeated).ShouldBeNull();
            detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
        }
        for (var turn = 0; turn < 2; turn++)
        {
            detector.Observe(repeated).ShouldBeNull();
            detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
        }
        detector.RetainedTurnCount.ShouldBe(2);

        if (unknownFirst) detector.Observe(unknown).ShouldBeNull();
        detector.Observe(repeated).ShouldBeNull();
        if (!unknownFirst) detector.Observe(unknown).ShouldBeNull();
        detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
        detector.RetainedTurnCount.ShouldBe(0);
        detector.PendingToolCount.ShouldBe(0);

        for (var turn = 1; turn <= 3; turn++)
        {
            detector.Observe(repeated).ShouldBeNull();
            var decision = detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0));
            if (turn < 3) decision.ShouldBeNull();
            else
            {
                var stop = decision.ShouldNotBeNull();
                stop.MatchedTurns.ShouldBe(3);
                stop.Reason.ShouldBe(repeatedKind == AgentActivityKind.ToolSucceeded
                    ? "unchanged-successful-evidence" : "repeated-failing-turn-sequence");
                stop.SupportingTurnIds.Count.ShouldBe(3);
            }
        }
    }

    [Fact]
    public void Observe_OversizedBatch_DoesNotInferLoopFromTruncatedEvidence()
    {
        var detector = new SubAgentNoProgressDetector(3, 3);
        for (var turn = 0; turn < 100; turn++)
        {
            for (var tool = 0; tool <= SubAgentNoProgressDetector.MaximumToolsPerTurn; tool++)
                detector.Observe(new(AgentActivityKind.ToolFailed, (ulong)tool, 0)).ShouldBeNull();
            detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
            detector.RetainedTurnCount.ShouldBeLessThanOrEqualTo(3);
            detector.PendingToolCount.ShouldBe(0);
        }
    }

    [Fact]
    public void Observe_DiverseSuccessfulEvidence_BoundsHistoryWithoutStopping()
    {
        var detector = new SubAgentNoProgressDetector(3, 3);
        for (ulong turn = 1; turn <= 1000; turn++)
        {
            detector.Observe(new(AgentActivityKind.ToolSucceeded, turn, 0)).ShouldBeNull();
            detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
            detector.RetainedSuccessCount.ShouldBeLessThanOrEqualTo(SubAgentNoProgressDetector.MaximumSuccessHistory);
        }
    }
}
