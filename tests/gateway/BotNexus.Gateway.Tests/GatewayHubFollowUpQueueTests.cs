using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Tests.Dispatching;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Behavioural coverage of <see cref="GatewayHub.FollowUp"/> for #2438: a follow-up must be
/// HELD while a run is in flight and only delivered once that run settles, while an idle agent
/// makes it an ordinary send.
/// </summary>
/// <remarks>
/// <para>
/// These assertions are deliberately made THROUGH THE HUB and not over
/// <c>Agent.FollowUp</c> in isolation. The agent's follow-up queue was always correct; the
/// defect was that <see cref="GatewayHub.FollowUp"/> never used it and instead called the
/// ordinary inbound dispatch path with kind <c>"message"</c>. A unit test over the agent queue
/// alone cannot catch that, which is exactly how the defect shipped.
/// </para>
/// <para>
/// <see cref="GatewayHub.FollowUp"/> is fire-and-forget: it schedules background work and
/// returns. Tests therefore await <see cref="GatewayHub.LastFollowUpDispatch"/> - a
/// deterministic completion handle - rather than sleeping. Every test below ends in an
/// unconditional assertion; none has an early return, conditional skip, or swallowed exception.
/// </para>
/// </remarks>
public sealed class GatewayHubFollowUpQueueTests
{
    private static readonly AgentId Agent = AgentId.From("agent-a");
    private static readonly SessionId Session = SessionId.From("sess-followup");

    /// <summary>Builds a supervisor whose <c>GetHandle</c> returns <paramref name="handle"/>.</summary>
    private static Mock<IAgentSupervisor> SupervisorFor(IAgentHandle? handle)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(handle!);
        return supervisor;
    }

    private static Mock<IAgentHandle> RunningHandleThatQueues(bool queued = true)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queued);
        return handle;
    }

    private static async Task DrainAsync(GatewayHub hub)
    {
        var dispatch = hub.LastFollowUpDispatch;
        Assert.NotNull(dispatch);
        await dispatch;
    }

    [Fact]
    public async Task FollowUp_WhileRunning_IsQueuedAndNotDispatchedAsAnOrdinaryMessage()
    {
        // THE core regression. Before the fix this dispatched immediately, which both polluted
        // the transcript mid-turn and pushed a message at an agent whose single-turn guard was
        // already held ("Agent is already running", #2388).
        var handle = RunningHandleThatQueues();
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = SignalRHubTests.CreateHub(
            supervisor: SupervisorFor(handle.Object).Object,
            orchestrator: orchestrator);

        await hub.FollowUp(Agent, Session, "hold this");
        await DrainAsync(hub);

        handle.Verify(h => h.TryFollowUpWhileRunningAsync("hold this", It.IsAny<CancellationToken>()), Times.Once);
        orchestrator.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task FollowUp_WhileRunning_PublishesFollowUpQueuedActivity()
    {
        // The user must be able to tell "held for later" from "sent now".
        var handle = RunningHandleThatQueues();
        var activity = new Mock<IActivityBroadcaster>();
        var hub = SignalRHubTests.CreateHub(
            supervisor: SupervisorFor(handle.Object).Object,
            activity: activity.Object);

        await hub.FollowUp(Agent, Session, "hold this");
        await DrainAsync(hub);

        activity.Verify(a => a.PublishAsync(
            It.Is<GatewayActivity>(ga => ga.Type == GatewayActivityType.FollowUpQueued
                                         && ga.AgentId == Agent.Value
                                         && ga.SessionId == Session.Value),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FollowUp_WhenAgentIdle_DispatchesImmediatelyAsNormalMessage()
    {
        // An idle agent has nothing to wait for, so a follow-up must NOT be held indefinitely.
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = SignalRHubTests.CreateHub(
            supervisor: SupervisorFor(handle.Object).Object,
            orchestrator: orchestrator);

        await hub.FollowUp(Agent, Session, "just send it");
        await DrainAsync(hub);

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.Content.ShouldBe("just send it");
        dispatched.Metadata["messageType"].ShouldBe("message");
    }

    [Fact]
    public async Task FollowUp_WhenNoHandleExists_DispatchesImmediatelyWithoutConjuringAHandle()
    {
        // No live handle means no run in flight. Creating one here would give the follow-up a
        // queue that is never drained again (the same dead-letter trap the Steer path guards).
        var supervisor = SupervisorFor(null);
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = SignalRHubTests.CreateHub(supervisor: supervisor.Object, orchestrator: orchestrator);

        await hub.FollowUp(Agent, Session, "no handle");
        await DrainAsync(hub);

        supervisor.Verify(
            s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()),
            Times.Never);
        orchestrator.Captured.ShouldHaveSingleItem().Content.ShouldBe("no handle");
    }

    [Fact]
    public async Task FollowUp_WhenRunSettlesBeforeQueueing_FallsBackToNormalDispatch()
    {
        // The handle reports "not queued" when the run settled underneath it and it reclaimed
        // the message. The hub must then send it normally rather than leaving it stranded.
        var handle = RunningHandleThatQueues(queued: false);
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = SignalRHubTests.CreateHub(
            supervisor: SupervisorFor(handle.Object).Object,
            orchestrator: orchestrator);

        await hub.FollowUp(Agent, Session, "raced");
        await DrainAsync(hub);

        orchestrator.Captured.ShouldHaveSingleItem().Content.ShouldBe("raced");
    }

    [Fact]
    public async Task FollowUp_MultipleWhileRunning_PreservesOrderIntoTheQueue()
    {
        var seen = new List<string>();
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((m, _) => seen.Add(m))
            .ReturnsAsync(true);
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = SignalRHubTests.CreateHub(
            supervisor: SupervisorFor(handle.Object).Object,
            orchestrator: orchestrator);

        await hub.FollowUp(Agent, Session, "first");
        await DrainAsync(hub);
        await hub.FollowUp(Agent, Session, "second");
        await DrainAsync(hub);
        await hub.FollowUp(Agent, Session, "third");
        await DrainAsync(hub);

        seen.ShouldBe(["first", "second", "third"]);
        orchestrator.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task FollowUp_WhenQueueOverflows_SurfacesErrorAndDoesNotSilentlyDispatch()
    {
        // Overflow of the bounded queue must be visible. It must NOT be swallowed, and it must
        // NOT quietly fall through to the normal-send path (that would push the message at a
        // running agent - the exact loss this fix removes).
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BotNexus.Agent.Core.PendingMessageQueueFullException(64));
        var activity = new Mock<IActivityBroadcaster>();
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = SignalRHubTests.CreateHub(
            supervisor: SupervisorFor(handle.Object).Object,
            activity: activity.Object,
            orchestrator: orchestrator);

        await hub.FollowUp(Agent, Session, "overflow");
        await DrainAsync(hub);

        activity.Verify(a => a.PublishAsync(
            It.Is<GatewayActivity>(ga => ga.Type == GatewayActivityType.Error),
            It.IsAny<CancellationToken>()), Times.Once);
        orchestrator.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task FollowUp_NullOrWhitespaceContent_ThrowsArgumentException()
    {
        var hub = SignalRHubTests.CreateHub();

        Func<Task> act = () => hub.FollowUp(Agent, Session, "   ");

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task FollowUp_DoesNotInterruptTheRunningTurn()
    {
        // Steer interrupts; follow-up waits. A follow-up must never abort or steer.
        var handle = RunningHandleThatQueues();
        var hub = SignalRHubTests.CreateHub(supervisor: SupervisorFor(handle.Object).Object);

        await hub.FollowUp(Agent, Session, "do not interrupt");
        await DrainAsync(hub);

        handle.Verify(h => h.AbortAsync(It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.InterruptAndSteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
