using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Pins the delivery half of the #3028 seam: how a resolved steer/interrupt actually reaches a
/// running turn, and - just as importantly - what happens when it cannot.
/// </summary>
public sealed class AgentHandleSteerDelivererTests
{
    private static readonly AgentId Agent = AgentId.From("agent-1");
    private static readonly SessionId Session = SessionId.From("session-1");

    [Fact]
    public async Task TryDeliverAsync_SteerAgainstRunningTurn_InjectsAndRecordsHistory()
    {
        var handle = CreateHandle(running: true);
        var sessions = CreateSessionStore(out var session);
        var deliverer = CreateDeliverer(handle, sessions);

        var delivered = await deliverer.TryDeliverAsync(
            CreateMessage("redirect to the tests"),
            new InboundDeliveryDecision(InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, true));

        delivered.ShouldBeTrue();
        await handle.Received(1).SteerAsync("redirect to the tests", Arg.Any<CancellationToken>());
        await handle.DidNotReceive().InterruptAndSteerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The transcript must record what the agent was told, or the steered message is invisible in
        // history even though it changed the run's direction.
        session.History.ShouldContain(e => e.Role == MessageRole.User && e.Content == "redirect to the tests");
        await sessions.Received(1).SaveAsync(session, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Interrupt must take the abort-and-redirect path, not the plain steer path. Collapsing the two
    /// would make an explicit interrupt silently behave as an ordinary steer - it would appear to
    /// work while leaving the abandoned direction running.
    /// </summary>
    [Fact]
    public async Task TryDeliverAsync_InterruptAgainstRunningTurn_UsesInterruptPath()
    {
        var handle = CreateHandle(running: true);
        var sessions = CreateSessionStore(out _);
        var deliverer = CreateDeliverer(handle, sessions);

        var delivered = await deliverer.TryDeliverAsync(
            CreateMessage("stop, do this instead"),
            new InboundDeliveryDecision(InboundDeliveryMode.Interrupt, InboundDeliveryMode.Interrupt, true));

        delivered.ShouldBeTrue();
        await handle.Received(1).InterruptAndSteerAsync("stop, do this instead", Arg.Any<CancellationToken>());
        await handle.DidNotReceive().SteerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Sad path, and the reason the running re-check exists: the turn ended between the resolver's
    /// decision and this call. Injecting anyway would put the message into a queue nothing drains.
    /// It must report non-delivery AND write no history, so the orchestrator's queue fallback does
    /// not produce a duplicate entry.
    /// </summary>
    [Fact]
    public async Task TryDeliverAsync_TurnEndedSinceDecision_ReportsNotDeliveredAndWritesNoHistory()
    {
        var handle = CreateHandle(running: false);
        var sessions = CreateSessionStore(out var session);
        var deliverer = CreateDeliverer(handle, sessions);

        var delivered = await deliverer.TryDeliverAsync(
            CreateMessage("too late"),
            new InboundDeliveryDecision(InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, true));

        delivered.ShouldBeFalse();
        await handle.DidNotReceive().SteerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        session.History.ShouldBeEmpty();
        await sessions.DidNotReceive().SaveAsync(session, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryDeliverAsync_NoLiveHandle_ReportsNotDelivered()
    {
        // Unstubbed GetHandle returns null - that IS the condition under test. An Arg.Any spec
        // against the Vogen id parameters would be left unbound and later throw
        // RedundantArgumentMatcherException on an unrelated interaction.
        var supervisor = Substitute.For<IAgentSupervisor>();
        var sessions = CreateSessionStore(out _);

        var deliverer = new AgentHandleSteerDeliverer(
            supervisor, sessions, NullLogger<AgentHandleSteerDeliverer>.Instance);

        var delivered = await deliverer.TryDeliverAsync(
            CreateMessage("hello"),
            new InboundDeliveryDecision(InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, true));

        delivered.ShouldBeFalse();
    }

    /// <summary>
    /// Sad path: without both ids there is no single handle to steer. Guessing a target is the
    /// mis-route this issue exists to remove, so it refuses rather than picking one.
    /// </summary>
    [Fact]
    public async Task TryDeliverAsync_MessageWithoutSessionHint_RefusesWithoutConsultingSupervisor()
    {
        var supervisor = Substitute.For<IAgentSupervisor>();
        var sessions = CreateSessionStore(out _);
        var deliverer = new AgentHandleSteerDeliverer(
            supervisor, sessions, NullLogger<AgentHandleSteerDeliverer>.Instance);

        var message = new InboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hello"
        };

        var delivered = await deliverer.TryDeliverAsync(
            message,
            new InboundDeliveryDecision(InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, true));

        delivered.ShouldBeFalse();
        supervisor.DidNotReceive().GetHandle(Agent, Session);
    }

    /// <summary>
    /// The deliverer must never CREATE a handle as a side effect of delivering. Conjuring an idle
    /// handle is how the pre-guard hub path dead-lettered steers into sessions with no live run.
    /// </summary>
    [Fact]
    public async Task TryDeliverAsync_NeverCreatesAHandle()
    {
        var handle = CreateHandle(running: true);
        var sessions = CreateSessionStore(out _);
        var supervisor = Substitute.For<IAgentSupervisor>();
        supervisor.GetHandle(Agent, Session).Returns(handle);

        var deliverer = new AgentHandleSteerDeliverer(
            supervisor, sessions, NullLogger<AgentHandleSteerDeliverer>.Instance);

        await deliverer.TryDeliverAsync(
            CreateMessage("hi"),
            new InboundDeliveryDecision(InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, true));

        await supervisor.DidNotReceive().GetOrCreateAsync(
            Agent, Session, Arg.Any<CancellationToken>());
    }

    private static IAgentHandle CreateHandle(bool running)
    {
        var handle = Substitute.For<IAgentHandle>();
        handle.IsRunning.Returns(running);
        return handle;
    }

    private static AgentHandleSteerDeliverer CreateDeliverer(IAgentHandle handle, ISessionStore sessions)
    {
        var supervisor = Substitute.For<IAgentSupervisor>();
        supervisor.GetHandle(Agent, Session).Returns(handle);
        return new AgentHandleSteerDeliverer(
            supervisor, sessions, NullLogger<AgentHandleSteerDeliverer>.Instance);
    }

    private static ISessionStore CreateSessionStore(out GatewaySession session)
    {
        var created = new GatewaySession { SessionId = Session, AgentId = Agent };
        session = created;
        var sessions = Substitute.For<ISessionStore>();
        sessions
            .GetOrCreateAsync(Session, Agent, Arg.Any<CancellationToken>())
            .Returns(created);
        return sessions;
    }

    private static InboundMessage CreateMessage(string content)
        => new()
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = content,
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: Agent,
                RequestedSessionId: Session,
                RequestedConversationId: null,
                DeliveryMode: InboundDeliveryMode.Steer)
        };
}
