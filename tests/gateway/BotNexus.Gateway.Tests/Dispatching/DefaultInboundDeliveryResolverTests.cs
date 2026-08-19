using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Pins the server-side steer/queue decision (#3028 AC1, AC5). Every clause here is about the
/// DECISION only - <see cref="DefaultInboundDeliveryResolver"/> has no side effects, so these tests
/// need no live agent, which is precisely why the decision was split out of the delivery.
/// </summary>
public sealed class DefaultInboundDeliveryResolverTests
{
    private static readonly AgentId Agent = AgentId.From("agent-1");
    private static readonly SessionId Session = SessionId.From("session-1");

    /// <summary>
    /// AC5 (default): the documented default for a message arriving while a turn is ACTIVE is to
    /// queue, not to steer. This is the clause that would break if someone later decided Auto should
    /// "do the helpful thing" and inject into a busy session.
    /// </summary>
    [Fact]
    public async Task Auto_WhileTurnActive_ResolvesToQueue()
    {
        var resolver = CreateResolver(turnRunning: true);

        var decision = await resolver.ResolveAsync(CreateMessage(InboundDeliveryMode.Auto));

        decision.Resolved.ShouldBe(InboundDeliveryMode.Queue);
        decision.Requested.ShouldBe(InboundDeliveryMode.Auto);
        decision.FellBackToQueue.ShouldBeFalse(
            "Auto asked for no live-turn mechanism, so queueing is its intended outcome, not a downgrade");
    }

    [Fact]
    public async Task Auto_WhileIdle_ResolvesToQueue()
    {
        var resolver = CreateResolver(turnRunning: false);

        var decision = await resolver.ResolveAsync(CreateMessage(InboundDeliveryMode.Auto));

        decision.Resolved.ShouldBe(InboundDeliveryMode.Queue);
    }

    /// <summary>
    /// AC5 (non-default honoured): an explicit steer against a RUNNING turn resolves to Steer.
    /// </summary>
    [Fact]
    public async Task Steer_WhileTurnActive_ResolvesToSteer()
    {
        var resolver = CreateResolver(turnRunning: true);

        var decision = await resolver.ResolveAsync(CreateMessage(InboundDeliveryMode.Steer));

        decision.Resolved.ShouldBe(InboundDeliveryMode.Steer);
        decision.TurnWasActive.ShouldBeTrue();
        decision.FellBackToQueue.ShouldBeFalse();
    }

    [Fact]
    public async Task Interrupt_WhileTurnActive_ResolvesToInterrupt()
    {
        var resolver = CreateResolver(turnRunning: true);

        var decision = await resolver.ResolveAsync(CreateMessage(InboundDeliveryMode.Interrupt));

        decision.Resolved.ShouldBe(InboundDeliveryMode.Interrupt);
        decision.TurnWasActive.ShouldBeTrue();
    }

    /// <summary>
    /// Sad path: a steer with nothing running must NOT be injected into an idle handle's pending
    /// queue, because nothing would ever drain it. It degrades to Queue, and the degradation is
    /// observable via <see cref="InboundDeliveryDecision.FellBackToQueue"/> rather than silent.
    /// </summary>
    [Fact]
    public async Task Steer_WhileIdle_FallsBackToQueueObservably()
    {
        var resolver = CreateResolver(turnRunning: false);

        var decision = await resolver.ResolveAsync(CreateMessage(InboundDeliveryMode.Steer));

        decision.Resolved.ShouldBe(InboundDeliveryMode.Queue);
        decision.Requested.ShouldBe(InboundDeliveryMode.Steer);
        decision.TurnWasActive.ShouldBeFalse();
        decision.FellBackToQueue.ShouldBeTrue();
    }

    [Fact]
    public async Task Interrupt_WhenNoHandleExistsAtAll_FallsBackToQueue()
    {
        // No GetHandle setup at all: an unstubbed substitute returns null, which IS the
        // "no live handle" condition under test.
        var supervisor = Substitute.For<IAgentSupervisor>();
        var resolver = new DefaultInboundDeliveryResolver(supervisor);

        var decision = await resolver.ResolveAsync(CreateMessage(InboundDeliveryMode.Interrupt));

        decision.Resolved.ShouldBe(InboundDeliveryMode.Queue);
        decision.FellBackToQueue.ShouldBeTrue();
    }

    /// <summary>
    /// A steer that names no session has no single handle to target. Guessing one would reintroduce
    /// exactly the client-side mis-route this issue exists to remove, so it queues.
    /// </summary>
    [Fact]
    public async Task Steer_WithoutSessionHint_FallsBackToQueueAndNeverAsksSupervisor()
    {
        var supervisor = Substitute.For<IAgentSupervisor>();
        var resolver = new DefaultInboundDeliveryResolver(supervisor);
        var message = CreateMessage(InboundDeliveryMode.Steer, includeSession: false);

        var decision = await resolver.ResolveAsync(message);

        decision.Resolved.ShouldBe(InboundDeliveryMode.Queue);
        supervisor.DidNotReceive().GetHandle(Agent, Session);
    }

    /// <summary>
    /// Auto is the overwhelmingly common path; it must not pay for a supervisor lookup it cannot
    /// act on. This also pins that Auto's answer genuinely does not depend on turn state.
    /// </summary>
    [Fact]
    public async Task Auto_NeverConsultsSupervisor()
    {
        var supervisor = Substitute.For<IAgentSupervisor>();
        var resolver = new DefaultInboundDeliveryResolver(supervisor);

        await resolver.ResolveAsync(CreateMessage(InboundDeliveryMode.Auto));

        supervisor.DidNotReceive().GetHandle(Agent, Session);
    }

    /// <summary>
    /// A message with no routing hints at all (the shape most channel adapters produce) must behave
    /// exactly like Auto - this is the behaviour-parity guard for every pre-#3028 writer site.
    /// </summary>
    [Fact]
    public async Task MessageWithoutRoutingHints_ResolvesToQueue()
    {
        var resolver = CreateResolver(turnRunning: true);
        var message = new InboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hello"
        };

        var decision = await resolver.ResolveAsync(message);

        decision.Resolved.ShouldBe(InboundDeliveryMode.Queue);
        decision.Requested.ShouldBe(InboundDeliveryMode.Auto);
    }

    [Fact]
    public async Task ResolveAsync_NullMessage_Throws()
    {
        var resolver = CreateResolver(turnRunning: false);

        await Should.ThrowAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!));
    }

    /// <summary>
    /// The resolver's contract says it never returns Auto: Auto is an input intent and the whole job
    /// is to collapse it to a mechanism a caller can act on.
    /// </summary>
    [Theory]
    [InlineData(InboundDeliveryMode.Auto)]
    [InlineData(InboundDeliveryMode.Queue)]
    [InlineData(InboundDeliveryMode.Steer)]
    [InlineData(InboundDeliveryMode.Interrupt)]
    public async Task ResolveAsync_NeverReturnsAuto(InboundDeliveryMode requested)
    {
        var resolver = CreateResolver(turnRunning: true);

        var decision = await resolver.ResolveAsync(CreateMessage(requested));

        decision.Resolved.ShouldNotBe(InboundDeliveryMode.Auto);
    }

    private static DefaultInboundDeliveryResolver CreateResolver(bool turnRunning)
    {
        var handle = Substitute.For<IAgentHandle>();
        handle.IsRunning.Returns(turnRunning);
        var supervisor = Substitute.For<IAgentSupervisor>();
        // Concrete Vogen values, never Arg.Any: an arg spec against a Vogen value object is left
        // unbound by NSubstitute and later explodes as RedundantArgumentMatcherException on an
        // unrelated interaction. The same trap is documented in ConversationMessagesControllerTests.
        supervisor.GetHandle(Agent, Session).Returns(handle);
        return new DefaultInboundDeliveryResolver(supervisor);
    }

    private static InboundMessage CreateMessage(
        InboundDeliveryMode mode, bool includeSession = true)
        => new()
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hello",
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: Agent,
                RequestedSessionId: includeSession ? Session : null,
                RequestedConversationId: null,
                DeliveryMode: mode)
        };
}
