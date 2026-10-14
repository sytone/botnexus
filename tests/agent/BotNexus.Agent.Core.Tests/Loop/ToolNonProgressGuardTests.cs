using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

public sealed class ToolNonProgressGuardTests
{
    private static readonly ToolCallContent Call = new("call", "probe", new Dictionary<string, object?>());
    private static readonly ToolResultAgentMessage Result = new("call", "probe",
        new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "private payload")]));

    private static Task<ToolNonProgressObservation> Observe(ToolNonProgressGuard guard)
        => guard.ObserveAsync([Call], [Result], CancellationToken.None);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ClassifiedCycles_RequireActualRepetitionAndStopWithinTwelveResults(int period)
    {
        var index = 0;
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(
            ToolProgressDecision.NoProgress($"scope-{index++ % period}", "same", "research")));
        ToolNonProgressObservation? observation = null;
        for (var i = 0; i < 12; i++)
        {
            observation = await Observe(guard);
            if (i < period) observation.ShouldStop.ShouldBeFalse();
            if (observation.ShouldStop) break;
        }
        var stopped = observation.ShouldNotBeNull();
        stopped.ShouldStop.ShouldBeTrue();
        stopped.TotalResults.ShouldBe(Math.Max(6, 2 * period));
    }

    [Fact]
    public async Task NearMissCycle_DoesNotTreatPartialRepetitionAsACompleteCycle()
    {
        var scopes = new[] { "A", "B", "C", "D", "A", "B", "C", "X" };
        var index = 0;
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(
            ToolProgressDecision.NoProgress(scopes[index++], "same", "research")));
        foreach (var scope in scopes)
        {
            _ = scope;
            (await Observe(guard)).ShouldStop.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ChangedEvidenceNearCycleBoundary_ResetsInsteadOfStopping()
    {
        var index = 0;
        var guard = new ToolNonProgressGuard((_, _) =>
        {
            var next = index++;
            return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress(
                $"scope-{next % 4}", next == 7 ? "changed" : "same", "research"));
        });
        for (var i = 0; i < 8; i++)
            (await Observe(guard)).ShouldStop.ShouldBeFalse();
        guard.RecentOutcomeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeutralOrNull_DoesNotIncrementOrResetKnownEvidence(bool explicitNeutral)
    {
        ToolProgressDecision? decision = ToolProgressDecision.NoProgress("scope", "same", "wait");
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(decision));
        for (var i = 0; i < 5; i++) await Observe(guard);
        decision = explicitNeutral ? ToolProgressDecision.Neutral : null;
        var neutral = await Observe(guard);
        neutral.ConsecutiveCount.ShouldBe(5);
        neutral.WarningReady.ShouldBeFalse();
        decision = ToolProgressDecision.NoProgress("scope", "same", "wait");
        (await Observe(guard)).ShouldStop.ShouldBeTrue();
    }

    [Fact]
    public async Task MalformedNoProgressAndInvalidOutcome_AreRejected()
    {
        foreach (var decision in new[] { new ToolProgressDecision(false), new(false, "scope", "", "wait"), new(false, "scope", "same", "") })
        {
            var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(decision));
            await Should.ThrowAsync<InvalidOperationException>(() => Observe(guard));
        }
        Should.Throw<ArgumentOutOfRangeException>(() => new ToolProgressDecision((ToolProgressOutcome)99));
        new ToolProgressDecision(true).Outcome.ShouldBe(ToolProgressOutcome.Progress);
        new ToolProgressDecision(false, "scope", "same", "wait").Outcome.ShouldBe(ToolProgressOutcome.NoProgress);
        ToolProgressDecision.Neutral.IsProgress.ShouldBeFalse();
        ToolProgressDecision.Neutral.Outcome.ShouldBe(ToolProgressOutcome.Neutral);
        var neutralGuard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(
            new ToolProgressDecision(ToolProgressOutcome.Neutral)));
        (await Observe(neutralGuard)).ConsecutiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task UnknownCallPair_IsNeutral_AndCancellationPropagates()
    {
        var guard = new ToolNonProgressGuard((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress("scope", "same", "wait"));
        });
        for (var i = 0; i < 5; i++) await Observe(guard);
        (await guard.ObserveAsync([], [Result], CancellationToken.None)).ConsecutiveCount.ShouldBe(5);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => guard.ObserveAsync([Call], [Result], cancellation.Token));
    }

    [Fact]
    public async Task RecentHistoryAndScopeEvidence_AreBoundedAcrossUniqueResearch()
    {
        var index = 0;
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(
            ToolProgressDecision.NoProgress($"target-{index++}", "stable", "research")));
        for (var i = 0; i < 120; i++)
        {
            (await Observe(guard)).ShouldStop.ShouldBeFalse();
            guard.RecentOutcomeCount.ShouldBeLessThanOrEqualTo(ToolNonProgressGuard.RecentOutcomeLimit);
            guard.ObservedScopeCount.ShouldBeLessThanOrEqualTo(ToolNonProgressGuard.ObservedScopeLimit);
        }
        guard.Reset();
        guard.RecentOutcomeCount.ShouldBe(0);
        guard.ObservedScopeCount.ShouldBe(0);
    }

    [Fact]
    public async Task DiverseStatusOnlyScopes_StopAtSixWithoutARepeatedIdentity()
    {
        var index = 0;
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(
            ToolProgressDecision.NoProgress($"scope-{index++}", "same", "unchanged-housekeeping")));
        for (var i = 0; i < 5; i++) (await Observe(guard)).ShouldStop.ShouldBeFalse();
        (await Observe(guard)).ShouldStop.ShouldBeTrue();
    }

    [Fact]
    public async Task UniqueResearchTargets_DoNotStopSolelyBecauseTheyAreClassified()
    {
        var index = 0;
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(
            ToolProgressDecision.NoProgress($"target-{index++}", "stable", "research")));
        for (var i = 0; i < 100; i++)
            (await Observe(guard)).ShouldStop.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangedEvidenceAndExplicitProgress_ResetKnownRepetitions()
    {
        ToolProgressDecision? decision = ToolProgressDecision.NoProgress("scope", "old", "wait");
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(decision));
        for (var i = 0; i < 5; i++) await Observe(guard);
        decision = ToolProgressDecision.NoProgress("scope", "new", "wait");
        (await Observe(guard)).ConsecutiveCount.ShouldBe(1);
        decision = ToolProgressDecision.Progress;
        (await Observe(guard)).ConsecutiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResetNeverClearsTheAbsoluteFuse_AndAllBatchResultsAreCounted()
    {
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(null));
        for (var i = 0; i < 127; i++)
        {
            (await Observe(guard)).ShouldStop.ShouldBeFalse();
            guard.Reset();
        }
        var observation = await guard.ObserveAsync([Call], [Result, Result, Result], CancellationToken.None);
        observation.AbsoluteLimitReached.ShouldBeTrue();
        observation.TotalResults.ShouldBe(130);
    }

    [Fact]
    public async Task ChangedEvidenceAtEndOfBatch_InvalidatesPendingWarningAndStop()
    {
        var index = 0;
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(
            ToolProgressDecision.NoProgress("scope", ++index == 7 ? "new" : "same", "wait")));
        var observation = await guard.ObserveAsync([Call], Enumerable.Repeat(Result, 7).ToArray(), CancellationToken.None);
        observation.ShouldStop.ShouldBeFalse();
        observation.WarningReady.ShouldBeFalse();
        observation.ConsecutiveCount.ShouldBe(1);
        observation.TotalResults.ShouldBe(7);
    }

    [Fact]
    public async Task ProgressAtEndOfExecutedBatch_PreventsStaleStop()
    {
        var index = 0;
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(
            ++index == 7 ? ToolProgressDecision.Progress : ToolProgressDecision.NoProgress("scope", "same", "wait")));
        var observation = await guard.ObserveAsync([Call], Enumerable.Repeat(Result, 7).ToArray(), CancellationToken.None);
        observation.ShouldStop.ShouldBeFalse();
        observation.WarningReady.ShouldBeFalse();
        observation.TotalResults.ShouldBe(7);
    }
}
