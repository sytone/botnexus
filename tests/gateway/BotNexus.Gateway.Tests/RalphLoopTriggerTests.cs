using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Ralph;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the turn-end-driven re-trigger behaviour of a ralph conversation (issue #2818).
/// </summary>
/// <remarks>
/// <para>
/// These tests drive the real lifecycle seam the gateway already publishes:
/// <see cref="SessionLifecycleEventType.Closed"/>, which - despite its name - fires once per
/// <em>turn</em> from the streaming post-run finalizer (#2780). The trigger is a plain subscriber, so
/// removing the subscription (or the publication) reddens the re-trigger, iteration-budget and pause
/// tests here by name, which is the non-vacuity clause of acceptance criterion 11.
/// </para>
/// <para>
/// The iteration runner is faked so no provider is required; the fake records the seed prompt and the
/// session id it was asked to run, which is what the fresh-session and instruction-reread assertions
/// read.
/// </para>
/// </remarks>
public sealed class RalphLoopTriggerTests
{
    private static readonly AgentId Agent = AgentId.From("ralph-agent");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Records what each iteration was asked to do, and can be told to fail.</summary>
    private sealed class FakeRunner : IRalphIterationRunner
    {
        public List<string> Prompts { get; } = [];

        public List<int> Iterations { get; } = [];

        public bool Succeed { get; set; } = true;

        public Func<Conversation, Task>? OnIteration { get; set; }

        public CancellationToken LastToken { get; private set; }

        public async Task<bool> RunIterationAsync(
            Conversation conversation,
            string prompt,
            int iteration,
            CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            Iterations.Add(iteration);
            LastToken = cancellationToken;
            if (OnIteration is not null)
                await OnIteration(conversation);
            return Succeed;
        }
    }

    /// <summary>Minimal controllable clock; the loop's wall-clock ceiling must be testable without waiting.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class Harness
    {
        public InMemoryConversationStore Store { get; } = new();

        public FakeRunner Runner { get; } = new();

        public TestClock Clock { get; } = new(Start);

        public RalphLoopTrigger Trigger { get; }

        public Harness()
        {
            Trigger = new RalphLoopTrigger(
                Store,
                Runner,
                NullLogger<RalphLoopTrigger>.Instance,
                lifecycleEvents: null,
                timeProvider: Clock);
        }

        public async Task<Conversation> SeedAsync(string instructions, RalphLoopConfig? config = null)
        {
            var conversation = ConversationFactory.CreateForRalph(
                ConversationId.From($"conv:{Guid.NewGuid():N}"),
                Agent,
                instructions,
                config,
                timestamp: Start);
            await Store.CreateAsync(conversation);
            return conversation;
        }

        /// <summary>Simulates the gateway publishing turn end for a session in this conversation.</summary>
        public Task TurnEndAsync(Conversation conversation)
        {
            var session = new GatewaySession { SessionId = SessionId.From($"s:{Guid.NewGuid():N}"), ConversationId = conversation.ConversationId };
            session.HydrateAgentId(Agent);
            return Trigger.OnSessionChangedAsync(
                new SessionLifecycleEvent(session.SessionId.Value, Agent.Value, SessionLifecycleEventType.Closed, session),
                CancellationToken.None);
        }

        public async Task<RalphLoopState> StateAsync(Conversation conversation)
        {
            var reloaded = await Store.GetAsync(conversation.ConversationId);
            return RalphLoopMetadata.Read(reloaded!).State;
        }
    }

    // ── AC2: turn end seeds a new session with the conversation instructions ──

    [Fact]
    public async Task TurnEnd_InRalphConversation_StartsAnIterationSeededWithTheInstructions()
    {
        var harness = new Harness();
        var conversation = await harness.SeedAsync("run the maintenance pass");

        await harness.TurnEndAsync(conversation);

        harness.Runner.Prompts.ShouldBe(["run the maintenance pass"]);
        harness.Runner.Iterations.ShouldBe([1]);
    }

    [Fact]
    public async Task TurnEnd_InNonRalphConversation_DoesNotRetrigger()
    {
        var harness = new Harness();
        var conversation = ConversationFactory.CreateForChannel(
            ConversationId.From("conv:human"), Agent, instructions: "not a loop");
        await harness.Store.CreateAsync(conversation);

        await harness.TurnEndAsync(conversation);

        harness.Runner.Prompts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(SessionLifecycleEventType.Created)]
    [InlineData(SessionLifecycleEventType.MessageAdded)]
    [InlineData(SessionLifecycleEventType.Expired)]
    [InlineData(SessionLifecycleEventType.Deleted)]
    public async Task NonTurnEndLifecycleEvents_DoNotRetrigger(SessionLifecycleEventType type)
    {
        // AC10: re-triggering is driven by turn end, never by any other signal or a timer.
        var harness = new Harness();
        var conversation = await harness.SeedAsync("work");
        var session = new GatewaySession { SessionId = SessionId.From("s:1"), ConversationId = conversation.ConversationId };
        session.HydrateAgentId(Agent);

        await harness.Trigger.OnSessionChangedAsync(
            new SessionLifecycleEvent("s:1", Agent.Value, type, session), CancellationToken.None);

        harness.Runner.Prompts.ShouldBeEmpty();
    }

    // ── AC4: instructions are re-read every iteration ────────────────────────

    [Fact]
    public async Task EditingInstructionsBetweenIterations_ChangesTheNextIterationPrompt()
    {
        var harness = new Harness();
        var conversation = await harness.SeedAsync("first instruction");

        await harness.TurnEndAsync(conversation);

        var reloaded = await harness.Store.GetAsync(conversation.ConversationId);
        reloaded!.Instructions = "second instruction";
        await harness.Store.SaveAsync(reloaded);

        await harness.TurnEndAsync(conversation);

        harness.Runner.Prompts.ShouldBe(["first instruction", "second instruction"]);
    }

    // ── AC5: maxIterations ───────────────────────────────────────────────────

    [Fact]
    public async Task MaxIterations_StartsExactlyThatManySessionsAndRecordsTheStopReason()
    {
        var harness = new Harness();
        var conversation = await harness.SeedAsync("work", new RalphLoopConfig(MaxIterations: 3));

        for (var i = 0; i < 4; i++)
            await harness.TurnEndAsync(conversation);

        harness.Runner.Iterations.ShouldBe([1, 2, 3]);

        var state = await harness.StateAsync(conversation);
        state.Iterations.ShouldBe(3);
        state.StopReason.ShouldBe(RalphStopReason.MaxIterations);
        state.StopDetail.ShouldNotBeNull();
        state.StopDetail!.ShouldContain("maxIterations=3");
    }

    // ── AC6: maxDurationMinutes, independently ───────────────────────────────

    [Fact]
    public async Task MaxDuration_BindsIndependentlyOfIterationsAndNamesItself()
    {
        var harness = new Harness();
        var conversation = await harness.SeedAsync(
            "work", new RalphLoopConfig(MaxIterations: 100, MaxDurationMinutes: 10));

        await harness.TurnEndAsync(conversation);
        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        await harness.TurnEndAsync(conversation);

        harness.Runner.Iterations.ShouldBe([1]);

        var state = await harness.StateAsync(conversation);
        state.StopReason.ShouldBe(RalphStopReason.MaxDuration);
        state.StopDetail!.ShouldContain("maxDurationMinutes=10");
    }

    // ── AC7: consecutive-failure circuit breaker ─────────────────────────────

    [Fact]
    public async Task AfterThreeConsecutiveFailedTurns_TheLoopHaltsWithFailedAndOnlyResumesExplicitly()
    {
        var harness = new Harness();
        var conversation = await harness.SeedAsync("work");
        harness.Runner.Succeed = false;

        // Each turn end runs an iteration that fails; the runner's failure is what feeds the breaker.
        await harness.Trigger.AdvanceAsync(conversation.ConversationId);
        await harness.Trigger.AdvanceAsync(conversation.ConversationId);
        await harness.Trigger.AdvanceAsync(conversation.ConversationId);

        (await harness.StateAsync(conversation)).ConsecutiveFailures.ShouldBe(3);

        var blocked = await harness.Trigger.AdvanceAsync(conversation.ConversationId);
        blocked.ShouldContinue.ShouldBeFalse();
        blocked.Reason.ShouldBe(RalphStopReason.Failed);
        (await harness.StateAsync(conversation)).StopReason.ShouldBe(RalphStopReason.Failed);

        harness.Runner.Succeed = true;
        await harness.Trigger.ResumeAsync(conversation.ConversationId);
        var resumed = await harness.Trigger.AdvanceAsync(conversation.ConversationId);
        resumed.ShouldContinue.ShouldBeTrue();
    }

    // ── AC8: agent-signalled pause ───────────────────────────────────────────

    [Fact]
    public async Task Pause_StopsRetriggeringAndIsDistinguishableFromRunningAndStopped()
    {
        var harness = new Harness();
        var conversation = await harness.SeedAsync("work");

        await harness.TurnEndAsync(conversation);
        (await harness.StateAsync(conversation)).StopReason.ShouldBe(RalphStopReason.None); // running

        await harness.Trigger.PauseAsync(conversation.ConversationId);
        await harness.TurnEndAsync(conversation);

        harness.Runner.Iterations.ShouldBe([1]);
        var paused = await harness.StateAsync(conversation);
        paused.IsPaused.ShouldBeTrue();
        paused.StopReason.ShouldBe(RalphStopReason.Paused);
        paused.StopReason.ShouldNotBe(RalphStopReason.Killed);

        await harness.Trigger.ResumeAsync(conversation.ConversationId);
        await harness.TurnEndAsync(conversation);
        harness.Runner.Iterations.ShouldBe([1, 2]);
    }

    // ── AC9: archive / kill switch ───────────────────────────────────────────

    [Fact]
    public async Task ArchivingARunningLoop_StopsRetriggering()
    {
        var harness = new Harness();
        var conversation = await harness.SeedAsync("work");

        await harness.TurnEndAsync(conversation);
        await harness.Store.ArchiveAsync(conversation.ConversationId);
        await harness.TurnEndAsync(conversation);

        harness.Runner.Iterations.ShouldBe([1]);
        (await harness.StateAsync(conversation)).StopReason.ShouldBe(RalphStopReason.NotActive);
    }

    [Fact]
    public async Task KillSwitch_CancelsTheInFlightIterationRatherThanWaitingForIt()
    {
        // AC9: stopping must not wait for the in-flight turn to complete.
        var harness = new Harness();
        var conversation = await harness.SeedAsync("work");

        harness.Runner.OnIteration = conv =>
        {
            harness.Trigger.Kill(conv.ConversationId);
            return Task.CompletedTask;
        };

        await harness.Trigger.AdvanceAsync(conversation.ConversationId);

        harness.Runner.LastToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task KillSwitch_RecordsKilledAndBlocksFurtherIterations()
    {
        var harness = new Harness();
        var conversation = await harness.SeedAsync("work");

        await harness.Trigger.AdvanceAsync(conversation.ConversationId);
        await harness.Trigger.KillAsync(conversation.ConversationId);
        var decision = await harness.Trigger.AdvanceAsync(conversation.ConversationId);

        decision.ShouldContinue.ShouldBeFalse();
        decision.Reason.ShouldBe(RalphStopReason.Killed);
        harness.Runner.Iterations.ShouldBe([1]);
        (await harness.StateAsync(conversation)).StopReason.ShouldBe(RalphStopReason.Killed);
    }
}
