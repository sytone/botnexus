using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Pins that the orchestrator ACTS on the #3028 delivery decision rather than merely computing it:
/// a steered message must reach the steer deliverer and must NOT reach the queue processor, and the
/// default must still reach the processor exactly as before.
/// </summary>
/// <remarks>
/// These are the tests that would have failed against pre-#3028 <c>main</c>, where
/// <c>grep -n 'Steer' DefaultInboundMessageOrchestrator.cs</c> returned nothing.
/// </remarks>
public sealed class OrchestratorSteeringRoutingTests
{
    private static readonly IReadOnlyList<DispatchResult> EmptyDispatches = Array.Empty<DispatchResult>();
    private static readonly AgentId Agent = AgentId.From("agent-1");
    private static readonly SessionId Session = SessionId.From("session-1");

    /// <summary>
    /// AC5 (non-default honoured end-to-end): an explicit steer against a running turn is injected
    /// into that turn and never enters the FIFO queue, so no second agent run is scheduled.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_SteerWhileTurnActive_DeliversToSteererAndBypassesQueue()
    {
        var processor = CreateProcessor();
        var deliverer = Substitute.For<IInboundSteerDeliverer>();
        deliverer
            .TryDeliverAsync(Arg.Any<InboundMessage>(), Arg.Any<InboundDeliveryDecision>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var orchestrator = CreateOrchestrator(
            processor, deliverer, new InboundDeliveryDecision(
                InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, TurnWasActive: true));

        var result = await orchestrator.AcceptAsync(CreateMessage(InboundDeliveryMode.Steer));

        result.Status.ShouldBe(InboundDispatchStatus.Steered);
        await deliverer.Received(1).TryDeliverAsync(
            Arg.Any<InboundMessage>(), Arg.Any<InboundDeliveryDecision>(), Arg.Any<CancellationToken>());
        await processor.DidNotReceive().ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AC5 (documented default): the default intent queues even with the steering seam fully wired,
    /// so the deliverer is never consulted and the processor runs a normal turn.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_AutoWhileTurnActive_QueuesAndNeverSteers()
    {
        var processor = CreateProcessor();
        var deliverer = Substitute.For<IInboundSteerDeliverer>();

        var orchestrator = CreateOrchestrator(
            processor, deliverer, new InboundDeliveryDecision(
                InboundDeliveryMode.Auto, InboundDeliveryMode.Queue, TurnWasActive: true));

        var result = await orchestrator.AcceptAsync(CreateMessage(InboundDeliveryMode.Auto));

        result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        await processor.Received(1).ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
        await deliverer.DidNotReceive().TryDeliverAsync(
            Arg.Any<InboundMessage>(), Arg.Any<InboundDeliveryDecision>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Sad path: the turn can end between the resolver's check and the injection. A deliverer that
    /// reports it could not inject must NOT lose the message - it falls through to the queue and
    /// gets a turn of its own.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_SteerDelivererReportsNotDelivered_FallsBackToQueue()
    {
        var processor = CreateProcessor();
        var deliverer = Substitute.For<IInboundSteerDeliverer>();
        deliverer
            .TryDeliverAsync(Arg.Any<InboundMessage>(), Arg.Any<InboundDeliveryDecision>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var orchestrator = CreateOrchestrator(
            processor, deliverer, new InboundDeliveryDecision(
                InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, TurnWasActive: true));

        var result = await orchestrator.AcceptAsync(CreateMessage(InboundDeliveryMode.Steer));

        result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        await processor.Received(1).ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Sad path: a steer path that THROWS must degrade to the historical queue behaviour rather than
    /// failing an inbound message that would otherwise have been delivered fine.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_SteerDelivererThrows_FallsBackToQueueWithoutPropagating()
    {
        var processor = CreateProcessor();
        var deliverer = Substitute.For<IInboundSteerDeliverer>();
        deliverer
            .TryDeliverAsync(Arg.Any<InboundMessage>(), Arg.Any<InboundDeliveryDecision>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("steer path broken"));

        var orchestrator = CreateOrchestrator(
            processor, deliverer, new InboundDeliveryDecision(
                InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, TurnWasActive: true));

        var result = await orchestrator.AcceptAsync(CreateMessage(InboundDeliveryMode.Steer));

        result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        await processor.Received(1).ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Sad path: a resolver that throws must not take the inbound path down with it.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_ResolverThrows_FallsBackToQueue()
    {
        var processor = CreateProcessor();
        var deliverer = Substitute.For<IInboundSteerDeliverer>();
        var resolver = Substitute.For<IInboundDeliveryResolver>();
        resolver
            .ResolveAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns<Task<InboundDeliveryDecision>>(_ => throw new InvalidOperationException("resolver broken"));

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance,
            channelManager: null, queueCapacity: 64,
            deliveryResolver: resolver, steerDeliverer: deliverer);

        var result = await orchestrator.AcceptAsync(CreateMessage(InboundDeliveryMode.Steer));

        result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        await processor.Received(1).ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AC6 behaviour parity: an orchestrator constructed WITHOUT the seam - which is every existing
    /// host and test call site - must queue unconditionally, even for a message that explicitly asks
    /// to steer. The steering path is opt-in at composition time, never implicit.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_WithoutSeamWired_QueuesEvenAnExplicitSteer()
    {
        var processor = CreateProcessor();

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var result = await orchestrator.AcceptAsync(CreateMessage(InboundDeliveryMode.Steer));

        result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        await processor.Received(1).ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
    }

    private static IInboundMessageProcessor CreateProcessor()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome(EmptyDispatches, ShouldClosePerSessionQueue: false));
        return processor;
    }

    private static DefaultInboundMessageOrchestrator CreateOrchestrator(
        IInboundMessageProcessor processor,
        IInboundSteerDeliverer deliverer,
        InboundDeliveryDecision decision)
    {
        var resolver = Substitute.For<IInboundDeliveryResolver>();
        resolver
            .ResolveAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(decision);

        return new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance,
            channelManager: null, queueCapacity: 64,
            deliveryResolver: resolver, steerDeliverer: deliverer);
    }

    private static InboundMessage CreateMessage(InboundDeliveryMode mode)
        => new()
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hello",
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: Agent,
                RequestedSessionId: Session,
                RequestedConversationId: null,
                DeliveryMode: mode)
        };
}
