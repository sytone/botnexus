using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Dispatching;

public sealed class UnifiedInboundActivityStreamTests
{
    private static readonly AgentId Agent = AgentId.From("agent-1");
    private static readonly SessionId Session = SessionId.From("session-1");
    private static readonly ConversationId Conversation = ConversationId.From("conversation-1");

    [Theory]
    [InlineData(InboundDeliveryMode.Auto, InboundDeliveryMode.Queue, false)]
    [InlineData(InboundDeliveryMode.Queue, InboundDeliveryMode.Queue, false)]
    [InlineData(InboundDeliveryMode.Steer, InboundDeliveryMode.Steer, true)]
    [InlineData(InboundDeliveryMode.Interrupt, InboundDeliveryMode.Interrupt, true)]
    public async Task AcceptAsync_PublishesEveryRequestedIntentThroughOneActivityStream(
        InboundDeliveryMode requested,
        InboundDeliveryMode resolved,
        bool deliveredAsSteer)
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome([], ShouldClosePerSessionQueue: false));
        var resolver = Substitute.For<IInboundDeliveryResolver>();
        resolver.ResolveAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundDeliveryDecision(requested, resolved, deliveredAsSteer));
        var deliverer = Substitute.For<IInboundSteerDeliverer>();
        deliverer.TryDeliverAsync(
                Arg.Any<InboundMessage>(),
                Arg.Any<InboundDeliveryDecision>(),
                Arg.Any<CancellationToken>())
            .Returns(deliveredAsSteer);
        var activity = Substitute.For<IActivityBroadcaster>();

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance,
            deliveryResolver: resolver,
            steerDeliverer: deliverer,
            activityBroadcaster: activity);

        await orchestrator.AcceptAsync(CreateMessage(requested));

        await activity.Received(1).PublishAsync(
            Arg.Is<GatewayActivity>(item =>
                item.Type == GatewayActivityType.MessageReceived &&
                item.AgentId == Agent.Value &&
                item.SessionId == Session.Value &&
                item.ConversationId == Conversation.Value &&
                item.ChannelType == ChannelKey.From("test") &&
                item.Message == "hello" &&
                item.Data != null &&
                Equals(item.Data["requestedDeliveryIntent"], requested.ToString())),
            CancellationToken.None);
    }

    [Fact]
    public async Task AcceptAsync_WhenActivityPublicationFails_StillDeliversMessage()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome([], ShouldClosePerSessionQueue: false));
        var activity = Substitute.For<IActivityBroadcaster>();
        activity.PublishAsync(Arg.Any<GatewayActivity>(), CancellationToken.None)
            .Returns<ValueTask>(_ => throw new InvalidOperationException("activity unavailable"));

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance,
            activityBroadcaster: activity);

        var result = await orchestrator.AcceptAsync(CreateMessage(InboundDeliveryMode.Queue));

        result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        await processor.Received(1).ProcessAsync(
            Arg.Is<InboundMessage>(message => message.Content == "hello"),
            Arg.Any<CancellationToken>());
    }

    private static InboundMessage CreateMessage(InboundDeliveryMode mode) => new()
    {
        ChannelType = ChannelKey.From("test"),
        ChannelAddress = ChannelAddress.From("addr-1"),
        SenderId = "sender-1",
        Sender = CitizenId.Of(UserId.From("sender-1")),
        Content = "hello",
        RoutingHints = new InboundMessageRoutingHints(Agent, Session, Conversation, mode)
    };
}
