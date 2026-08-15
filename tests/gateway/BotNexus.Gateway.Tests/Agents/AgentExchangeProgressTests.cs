using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// #3176 — observability for agent-to-agent task handoff. Covers the three seams: the child
/// exchange linkage returned synchronously in the tool result, the progress events emitted into
/// the INITIATING conversation, and the discoverability of the child exchange from its parent.
/// </summary>
/// <remarks>
/// Progress is asserted through a capturing <see cref="IAgentExchangeProgressNotifier"/> — i.e.
/// against the messages actually emitted, not against an internal flag (AC3 says so explicitly).
/// </remarks>
public sealed class AgentExchangeProgressTests
{
    /// <summary>
    /// Records what the exchange published. This is the "capture emitted messages" harness AC3
    /// demands: it observes the same interface production delivery uses, so a regression that
    /// stops emitting is caught even though no channel is stood up.
    /// </summary>
    private sealed class CapturingProgressNotifier : IAgentExchangeProgressNotifier
    {
        public List<AgentExchangeProgressEvent> Events { get; } = [];

        public Task PublishAsync(AgentExchangeProgressEvent progressEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(progressEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>A notifier that always throws, to prove progress failures cannot break a handoff.</summary>
    private sealed class ThrowingProgressNotifier : IAgentExchangeProgressNotifier
    {
        public Task PublishAsync(AgentExchangeProgressEvent progressEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("progress sink exploded");
    }

    private static readonly AgentId Initiator = AgentId.From("initiator-agent");
    private static readonly AgentId Target = AgentId.From("target-agent");

    // ---- AC3: started + completed + failed are emitted ----

    [Fact]
    public async Task ConverseAsync_OnSuccess_EmitsStartedThenCompleted_NamingTheChildExchange()
    {
        var progress = new CapturingProgressNotifier();
        var (service, _, _) = BuildService(progress);

        var result = await service.ConverseAsync(Request());

        progress.Events.Count.ShouldBe(2, customMessage: "A successful handoff emits exactly started + completed.");
        progress.Events[0].Phase.ShouldBe(AgentExchangeProgressPhase.Started);
        progress.Events[1].Phase.ShouldBe(AgentExchangeProgressPhase.Completed);

        foreach (var e in progress.Events)
        {
            e.InitiatorId.ShouldBe(Initiator);
            e.TargetId.ShouldBe(Target);
            e.ChildConversationId.ShouldBe(result.ConversationId,
                customMessage: "Every event must name the child exchange - that reference is the " +
                    "whole observability payload.");
            e.ChildSessionId.ShouldBe(result.SessionId);
        }
    }

    [Fact]
    public async Task ConverseAsync_OnFailure_EmitsFailedEvent_CarryingTheErrorReason()
    {
        var progress = new CapturingProgressNotifier();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM upstream went bang"));
        var (service, _, _) = BuildService(progress, handle);

        await Should.ThrowAsync<InvalidOperationException>(() => service.ConverseAsync(Request()));

        progress.Events.Select(e => e.Phase).ShouldBe(
            [AgentExchangeProgressPhase.Started, AgentExchangeProgressPhase.Failed]);
        progress.Events[^1].Reason.ShouldBe("LLM upstream went bang");
    }

    [Fact]
    public async Task ConverseAsync_EmitsStarted_BeforeTheFirstPromptFires()
    {
        // "Has the target actually started?" is only answerable if the started event precedes the
        // work rather than being batched out with the terminal event at the end.
        var order = new List<string>();
        var progress = new Mock<IAgentExchangeProgressNotifier>();
        progress.Setup(p => p.PublishAsync(It.IsAny<AgentExchangeProgressEvent>(), It.IsAny<CancellationToken>()))
            .Returns((AgentExchangeProgressEvent e, CancellationToken _) =>
            {
                order.Add($"progress:{e.Phase}");
                return Task.CompletedTask;
            });

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                order.Add("prompt");
                return new AgentResponse { Content = "ok" };
            });

        var (service, _, _) = BuildService(progress.Object, handle);
        await service.ConverseAsync(Request());

        order.ShouldBe(["progress:Started", "prompt", "progress:Completed"]);
    }

    // ---- AC4: halted is distinguishable from completed ----

    [Fact]
    public async Task ConverseAsync_WhenTurnCapExhausted_EmitsHalted_NotCompleted()
    {
        var progress = new CapturingProgressNotifier();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "still working" });
        var (service, _, _) = BuildService(progress, handle);

        // An objective keeps the loop running past turn one, so MaxTurns is what ends it.
        await service.ConverseAsync(Request() with { Objective = "keep going", MaxTurns = 2 });

        var terminal = progress.Events[^1];
        terminal.Phase.ShouldBe(AgentExchangeProgressPhase.Halted,
            customMessage: "Exhausting the turn cap is a halt, not a completion - the target never " +
                "signalled it was done.");
        terminal.Reason.ShouldBe("maxTurnsReached",
            customMessage: "The halt must name its cause so a reader can tell WHY it stopped.");
    }

    [Fact]
    public async Task ConverseAsync_WhenBudgetRefusesAdmission_EmitsHalted_WithNoChildExchange()
    {
        // A budget refusal happens BEFORE any conversation is minted, so it is the one terminal
        // outcome with no child ids. Without this event the initiating thread would see a bare
        // exception and no indication a handoff was even attempted.
        var progress = new CapturingProgressNotifier();
        var budget = new AgentExchangeBudgetTracker(
            Options.Create(new AgentExchangeBudgetOptions { DailyTurnCap = 0 }),
            NullLogger<AgentExchangeBudgetTracker>.Instance);
        var (service, _, _) = BuildService(progress, budgetTracker: budget);

        await Should.ThrowAsync<InvalidOperationException>(() => service.ConverseAsync(Request()));

        var only = progress.Events.ShouldHaveSingleItem();
        only.Phase.ShouldBe(AgentExchangeProgressPhase.Halted);
        only.ChildConversationId.ShouldBeNull(
            customMessage: "No exchange was admitted, so there is no child conversation to name.");
        only.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- AC6: blocking-caller parity ----

    [Fact]
    public async Task ConverseAsync_ResultShape_IsIdentical_WithAndWithoutProgressNotifier()
    {
        var (withNotifier, _, _) = BuildService(new CapturingProgressNotifier());
        var (withoutNotifier, _, _) = BuildService(progressNotifier: null);

        var a = await withNotifier.ConverseAsync(Request());
        var b = await withoutNotifier.ConverseAsync(Request());

        // Ids are per-call by design; every other field of the blocking contract must match.
        a.Status.ShouldBe(b.Status);
        a.Turns.ShouldBe(b.Turns);
        a.FinalResponse.ShouldBe(b.FinalResponse);
        a.CompletionReason.ShouldBe(b.CompletionReason);
        a.FinishReason.ShouldBe(b.FinishReason);
        a.FinishSummary.ShouldBe(b.FinishSummary);
        a.Transcript.Select(t => (t.Role, t.Content))
            .ShouldBe(b.Transcript.Select(t => (t.Role, t.Content)));
    }

    [Fact]
    public async Task ConverseAsync_WhenProgressNotifierThrows_ExchangeStillSucceeds()
    {
        // Observability must never be able to fail the thing it observes.
        var (service, _, _) = BuildService(new ThrowingProgressNotifier());

        var result = await service.ConverseAsync(Request());

        result.Status.ShouldBe("sealed");
        result.FinalResponse.ShouldBe("ok");
    }

    // ---- AC7: eager-pin ordering preserved ----

    [Fact]
    public async Task ConverseAsync_StartedEvent_IsNeverEmittedBeforeTheChildSessionIsPinned()
    {
        // The orphan-visibility invariant, extended to the new emission site: the started event
        // advertises a child conversation, so if it fired before the pin a reader could follow the
        // reference to a session with no ConversationId.
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();
        ConversationId? pinnedAtEmission = null;

        var progress = new Mock<IAgentExchangeProgressNotifier>();
        progress.Setup(p => p.PublishAsync(It.IsAny<AgentExchangeProgressEvent>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentExchangeProgressEvent e, CancellationToken ct) =>
            {
                if (e.Phase == AgentExchangeProgressPhase.Started && e.ChildSessionId is { } sid)
                {
                    var s = await sessionStore.GetAsync(sid, ct);
                    pinnedAtEmission = s?.Session.ConversationId;
                }
            });

        var service = BuildServiceWith(sessionStore, conversationStore, DefaultHandle(), progress.Object, null);
        var result = await service.ConverseAsync(Request());

        pinnedAtEmission.ShouldNotBeNull(
            customMessage: "At started-emission time the child session must already exist in the store.");
        pinnedAtEmission!.Value.ShouldBe(result.ConversationId,
            customMessage: "The child session must already be pinned to the conversation the event " +
                "advertises - never observable as an orphan.");
    }

    // ---- AC1 + AC2: the tool result carries the linkage ----

    [Fact]
    public async Task AgentConverseTool_Result_ContainsNonEmptyChildConversationIdAndSessionId()
    {
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();
        var service = BuildServiceWith(sessionStore, conversationStore, DefaultHandle(), null, null);

        var callerSessionId = SessionId.Create();
        await sessionStore.GetOrCreateAsync(callerSessionId, Initiator);

        var tool = new AgentConverseTool(service, sessionStore, Initiator, callerSessionId);
        var toolResult = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = Target.Value,
            ["message"] = "please do the thing"
        });

        var payload = JsonDocument.Parse(toolResult.Content[0].Value).RootElement;

        // AC2: this is exactly what lets a delegating agent answer "where is that work happening?"
        // from its own tool result, with no second call to anyone.
        payload.TryGetProperty("conversationId", out var conversationId).ShouldBeTrue(
            customMessage: "The tool result must carry the child conversationId.");
        payload.TryGetProperty("sessionId", out var childSessionId).ShouldBeTrue(
            customMessage: "The tool result must carry the child sessionId.");
        conversationId.GetString().ShouldNotBeNullOrWhiteSpace();
        childSessionId.GetString().ShouldNotBeNullOrWhiteSpace();
    }

    // ---- AC5: the child exchange is discoverable from the parent ----

    [Fact]
    public async Task ConverseAsync_ChildExchange_IsResolvableFromTheParentConversation()
    {
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();
        var service = BuildServiceWith(sessionStore, conversationStore, DefaultHandle(), null, null);

        var parentConversationId = ConversationId.Create();
        var result = await service.ConverseAsync(Request() with
        {
            InitiatorConversationId = parentConversationId
        });

        // The existing participant-based query is the discovery mechanism: the exchange handshake
        // pre-registers both agents, so the initiator's conversation list already contains it.
        var forInitiator = await conversationStore.ListForCitizenAsync(CitizenId.Of(Initiator));
        var forTarget = await conversationStore.ListForCitizenAsync(CitizenId.Of(Target));

        forInitiator.ShouldContain(c => c.ConversationId == result.ConversationId);
        forTarget.ShouldContain(c => c.ConversationId == result.ConversationId,
            customMessage: "The target is registered as a participant, so the exchange resolves for " +
                "it too - that is what makes the handoff readable from both sides.");

        // ...and the back-pointer is what narrows that list to THIS parent exchange.
        var child = forInitiator.Single(c => c.ConversationId == result.ConversationId);
        child.Metadata.ShouldContainKey("parentConversationId");
        child.Metadata["parentConversationId"].ShouldBe(parentConversationId.Value);

        var resolvedFromParent = forInitiator
            .Where(c => c.Metadata.TryGetValue("parentConversationId", out var p)
                        && (p as string) == parentConversationId.Value)
            .ToList();
        resolvedFromParent.ShouldHaveSingleItem().ConversationId.ShouldBe(result.ConversationId);
    }

    [Fact]
    public async Task ConverseAsync_WithNoInitiatingConversation_StampsNoParentPointer()
    {
        // Cron-driven and test call sites have no parent thread. The absence must be an absent key,
        // not an empty string that a discovery query would then match on.
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();
        var service = BuildServiceWith(sessionStore, conversationStore, DefaultHandle(), null, null);

        var result = await service.ConverseAsync(Request());

        var child = await conversationStore.GetAsync(result.ConversationId);
        child!.Metadata.ShouldNotContainKey("parentConversationId");
    }

    // ---- helpers ----

    private static AgentExchangeRequest Request() => new()
    {
        InitiatorId = Initiator,
        TargetId = Target,
        Message = "Do a thing",
        MaxTurns = 1
    };

    private static Mock<IAgentHandle> DefaultHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok" });
        return handle;
    }

    private static (AgentExchangeService Service, InMemorySessionStore SessionStore, InMemoryConversationStore ConversationStore)
        BuildService(
            IAgentExchangeProgressNotifier? progressNotifier,
            Mock<IAgentHandle>? handle = null,
            AgentExchangeBudgetTracker? budgetTracker = null)
    {
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();
        var service = BuildServiceWith(sessionStore, conversationStore, handle ?? DefaultHandle(), progressNotifier, budgetTracker);
        return (service, sessionStore, conversationStore);
    }

    private static AgentExchangeService BuildServiceWith(
        InMemorySessionStore sessionStore,
        InMemoryConversationStore conversationStore,
        Mock<IAgentHandle> handle,
        IAgentExchangeProgressNotifier? progressNotifier,
        AgentExchangeBudgetTracker? budgetTracker)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(Target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        return new AgentExchangeService(
            CreateRegistry().Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            budgetTracker: budgetTracker,
            progressNotifier: progressNotifier);
    }

    private static Mock<IAgentRegistry> CreateRegistry()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(Initiator)).Returns(new AgentDescriptor
        {
            AgentId = Initiator,
            DisplayName = Initiator.Value,
            ApiProvider = "openai",
            ModelId = "gpt-test",
            SubAgentIds = [Target.Value]
        });
        registry.Setup(r => r.Get(Target)).Returns(new AgentDescriptor
        {
            AgentId = Target,
            DisplayName = Target.Value,
            ApiProvider = "openai",
            ModelId = "gpt-test"
        });
        registry.Setup(r => r.Contains(Initiator)).Returns(true);
        registry.Setup(r => r.Contains(Target)).Returns(true);
        return registry;
    }
}
