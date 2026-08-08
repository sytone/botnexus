using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Pins the ralph conversation kind's creation-time contract and the single stop-decision function
/// (issue #2818).
/// </summary>
/// <remarks>
/// The stop conditions are gateway-enforced, never prompt-requested, and every one of them is
/// evaluated by <see cref="RalphLoopPolicy.Evaluate"/> alone. These tests deliberately drive that one
/// function rather than any per-condition helper: if a future change re-introduces a second place
/// that decides whether the loop continues, these tests keep passing while the loop misbehaves, so
/// the fitness of "one decision" is maintained by the design, not by the assertions here.
/// </remarks>
public sealed class RalphConversationTests
{
    private static readonly ConversationId Id = ConversationId.From("conv:ralph-test");
    private static readonly AgentId Agent = AgentId.From("agent-ralph");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── AC1: creation-time validation ────────────────────────────────────────

    [Fact]
    public void CreateForRalph_WithInstructions_StampsRalphKindAndSeedsInstructions()
    {
        var conversation = ConversationFactory.CreateForRalph(Id, Agent, "keep the repo green", timestamp: Start);

        conversation.Kind.ShouldBe(ConversationKind.Ralph);
        conversation.Instructions.ShouldBe("keep the repo green");
        conversation.Status.ShouldBe(ConversationStatus.Active);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateForRalph_WithoutInstructions_IsRefusedNamingTheMissingField(string? instructions)
    {
        var ex = Should.Throw<ArgumentException>(
            () => ConversationFactory.CreateForRalph(Id, Agent, instructions));

        ex.ParamName.ShouldBe("instructions");
        ex.Message.ShouldContain("instructions");
    }

    [Fact]
    public void CreateForRalph_StampsLoopStateAndConfigOnMetadata()
    {
        var conversation = ConversationFactory.CreateForRalph(
            Id, Agent, "work", new RalphLoopConfig(MaxIterations: 3, MaxDurationMinutes: 30), timestamp: Start);

        var (config, state) = RalphLoopMetadata.Read(conversation);

        config.MaxIterations.ShouldBe(3);
        config.MaxDurationMinutes.ShouldBe(30);
        config.MaxConsecutiveFailures.ShouldBe(RalphLoopConfig.DefaultMaxConsecutiveFailures);
        state.Iterations.ShouldBe(0);
        state.StartedAt.ShouldBe(Start);
    }

    [Fact]
    public void RalphLoopConfig_DefaultsTheCircuitBreakerToThree()
        => RalphLoopConfig.Default.MaxConsecutiveFailures.ShouldBe(3);

    [Fact]
    public void RalphLoopMetadata_RoundTripsThroughSerialization()
    {
        var conversation = ConversationFactory.CreateForRalph(Id, Agent, "work", timestamp: Start);
        RalphLoopMetadata.Write(
            conversation,
            new RalphLoopConfig(MaxIterations: 7, MaxDurationMinutes: 11, MaxConsecutiveFailures: 5),
            new RalphLoopState(Iterations: 4, StartedAt: Start, ConsecutiveFailures: 2, IsPaused: true,
                StopReason: RalphStopReason.Paused, StopDetail: "paused"));

        var (config, state) = RalphLoopMetadata.Read(conversation);

        config.MaxIterations.ShouldBe(7);
        config.MaxDurationMinutes.ShouldBe(11);
        config.MaxConsecutiveFailures.ShouldBe(5);
        state.Iterations.ShouldBe(4);
        state.ConsecutiveFailures.ShouldBe(2);
        state.IsPaused.ShouldBeTrue();
        state.StopReason.ShouldBe(RalphStopReason.Paused);
        state.StopDetail.ShouldBe("paused");
    }

    [Fact]
    public void RalphLoopMetadata_OnUnparseableBlob_FallsBackToBoundedDefaults()
    {
        var conversation = ConversationFactory.CreateForRalph(Id, Agent, "work", timestamp: Start);
        conversation.Metadata[RalphLoopMetadata.Key] = "{ not json";

        var (config, state) = RalphLoopMetadata.Read(conversation);

        config.MaxConsecutiveFailures.ShouldBe(RalphLoopConfig.DefaultMaxConsecutiveFailures);
        state.Iterations.ShouldBe(0);
    }

    // ── The single stop-decision function ────────────────────────────────────

    private static RalphLoopDecision Evaluate(
        RalphLoopConfig config,
        RalphLoopState state,
        ConversationStatus status = ConversationStatus.Active,
        string? instructions = "do the work",
        ConversationKind kind = ConversationKind.Ralph,
        DateTimeOffset? now = null)
        => RalphLoopPolicy.Evaluate(kind, status, instructions, config, state, now ?? Start);

    [Fact]
    public void Evaluate_WithBudgetRemaining_Continues()
    {
        var decision = Evaluate(new RalphLoopConfig(MaxIterations: 3), new RalphLoopState(Iterations: 1, StartedAt: Start));

        decision.ShouldContinue.ShouldBeTrue();
        decision.Reason.ShouldBe(RalphStopReason.None);
        decision.Detail.ShouldBeNull();
    }

    [Fact]
    public void Evaluate_WhenIterationBudgetExhausted_StopsNamingMaxIterations()
    {
        var decision = Evaluate(new RalphLoopConfig(MaxIterations: 3), new RalphLoopState(Iterations: 3, StartedAt: Start));

        decision.ShouldContinue.ShouldBeFalse();
        decision.Reason.ShouldBe(RalphStopReason.MaxIterations);
        decision.Detail.ShouldNotBeNull();
        decision.Detail!.ShouldContain("maxIterations=3");
    }

    [Fact]
    public void Evaluate_WhenWallClockCeilingExceeded_StopsNamingMaxDuration()
    {
        // Iteration budget deliberately untouched: duration is enforced independently (AC6).
        var decision = Evaluate(
            new RalphLoopConfig(MaxIterations: 100, MaxDurationMinutes: 10),
            new RalphLoopState(Iterations: 1, StartedAt: Start),
            now: Start.AddMinutes(11));

        decision.ShouldContinue.ShouldBeFalse();
        decision.Reason.ShouldBe(RalphStopReason.MaxDuration);
        decision.Detail!.ShouldContain("maxDurationMinutes=10");
    }

    [Fact]
    public void Evaluate_WhenBothBudgetsBind_NamesTheIterationBudgetThatBoundFirst()
    {
        var decision = Evaluate(
            new RalphLoopConfig(MaxIterations: 2, MaxDurationMinutes: 10),
            new RalphLoopState(Iterations: 2, StartedAt: Start),
            now: Start.AddMinutes(1));

        decision.Reason.ShouldBe(RalphStopReason.MaxIterations);
    }

    [Fact]
    public void Evaluate_AfterDefaultConsecutiveFailures_StopsNamingFailed()
    {
        var decision = Evaluate(RalphLoopConfig.Default, new RalphLoopState(Iterations: 5, StartedAt: Start, ConsecutiveFailures: 3));

        decision.ShouldContinue.ShouldBeFalse();
        decision.Reason.ShouldBe(RalphStopReason.Failed);
        decision.Detail!.ShouldContain("3");
    }

    [Fact]
    public void Evaluate_BelowFailureThreshold_StillContinues()
    {
        var decision = Evaluate(RalphLoopConfig.Default, new RalphLoopState(Iterations: 5, StartedAt: Start, ConsecutiveFailures: 2));

        decision.ShouldContinue.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_WhenAgentSignalledPause_StopsNamingPaused()
    {
        var decision = Evaluate(RalphLoopConfig.Default, new RalphLoopState(Iterations: 1, StartedAt: Start, IsPaused: true));

        decision.ShouldContinue.ShouldBeFalse();
        decision.Reason.ShouldBe(RalphStopReason.Paused);
    }

    [Fact]
    public void Evaluate_WhenKilled_StopsNamingKilledEvenIfAnotherConditionAlsoBinds()
    {
        var decision = Evaluate(
            new RalphLoopConfig(MaxIterations: 1),
            new RalphLoopState(Iterations: 5, StartedAt: Start, IsKilled: true));

        decision.Reason.ShouldBe(RalphStopReason.Killed);
    }

    [Fact]
    public void Evaluate_WhenConversationArchived_StopsNamingNotActive()
    {
        var decision = Evaluate(RalphLoopConfig.Default, new RalphLoopState(Iterations: 1, StartedAt: Start),
            status: ConversationStatus.Archived);

        decision.ShouldContinue.ShouldBeFalse();
        decision.Reason.ShouldBe(RalphStopReason.NotActive);
    }

    [Fact]
    public void Evaluate_WhenInstructionsBlank_StopsNamingNoInstructions()
    {
        var decision = Evaluate(RalphLoopConfig.Default, new RalphLoopState(Iterations: 1, StartedAt: Start),
            instructions: "  ");

        decision.Reason.ShouldBe(RalphStopReason.NoInstructions);
    }

    [Theory]
    [InlineData(ConversationKind.HumanAgent)]
    [InlineData(ConversationKind.AgentAgent)]
    [InlineData(ConversationKind.AgentSubAgent)]
    public void Evaluate_ForNonRalphKinds_NeverContinues(ConversationKind kind)
    {
        var decision = Evaluate(RalphLoopConfig.Default, new RalphLoopState(Iterations: 0, StartedAt: Start), kind: kind);

        decision.ShouldContinue.ShouldBeFalse();
        decision.Reason.ShouldBe(RalphStopReason.NotRalph);
    }

    [Fact]
    public void Evaluate_EveryStopDecisionCarriesADisclosure()
    {
        // #2789: a silently-applied limit teaches the caller a false constant, so no stop may be
        // anonymous. This asserts the invariant across every condition rather than per-condition.
        RalphLoopDecision[] stops =
        [
            Evaluate(new RalphLoopConfig(MaxIterations: 1), new RalphLoopState(Iterations: 1, StartedAt: Start)),
            Evaluate(new RalphLoopConfig(MaxDurationMinutes: 1), new RalphLoopState(Iterations: 1, StartedAt: Start), now: Start.AddMinutes(2)),
            Evaluate(RalphLoopConfig.Default, new RalphLoopState(ConsecutiveFailures: 3, StartedAt: Start)),
            Evaluate(RalphLoopConfig.Default, new RalphLoopState(IsPaused: true, StartedAt: Start)),
            Evaluate(RalphLoopConfig.Default, new RalphLoopState(IsKilled: true, StartedAt: Start)),
            Evaluate(RalphLoopConfig.Default, new RalphLoopState(StartedAt: Start), status: ConversationStatus.Archived)
        ];

        foreach (var stop in stops)
        {
            stop.ShouldContinue.ShouldBeFalse();
            stop.Reason.ShouldNotBe(RalphStopReason.None);
            stop.Detail.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void ConversationKind_RalphIsAddedAtTheEndWithoutRenumberingExistingMembers()
    {
        // Existing values are persisted numerically; renumbering them silently re-labels stored rows.
        ((int)ConversationKind.HumanAgent).ShouldBe(0);
        ((int)ConversationKind.AgentAgent).ShouldBe(1);
        ((int)ConversationKind.AgentSubAgent).ShouldBe(2);
        ((int)ConversationKind.Ralph).ShouldBe(3);
    }
}
