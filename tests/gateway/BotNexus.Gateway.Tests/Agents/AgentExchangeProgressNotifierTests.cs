using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// #3176 seam 2 delivery contract: handoff progress reaches the initiating conversation over the
/// EXISTING outbound fan-out path, carries a status line rather than child transcript content, and
/// never throws back into the exchange.
/// </summary>
public sealed class AgentExchangeProgressNotifierTests
{
    private static readonly SessionId InitiatorSession = SessionId.From("s_initiator");
    private static readonly ConversationId InitiatorConversation = ConversationId.From("c_initiator");

    private static AgentExchangeProgressEvent Event(AgentExchangeProgressPhase phase = AgentExchangeProgressPhase.Started) => new()
    {
        Phase = phase,
        InitiatorId = AgentId.From("nova"),
        TargetId = AgentId.From("farnsworth"),
        InitiatorSessionId = InitiatorSession,
        InitiatorConversationId = InitiatorConversation,
        ChildConversationId = ConversationId.From("c_child"),
        ChildSessionId = SessionId.From("s_child")
    };

    [Fact]
    public async Task PublishAsync_DeliversStatusLine_ToTheInitiatingSession()
    {
        var deliverer = new Mock<IOutboundResponseDeliverer>();
        string? delivered = null;
        SessionId? deliveredTo = null;
        ConversationId? deliveredConversation = null;
        deliverer
            .Setup(d => d.FanOutAsync(It.IsAny<InboundMessage>(), It.IsAny<SessionId>(), It.IsAny<string?>(), It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .Callback((InboundMessage _, SessionId s, string? c, ConversationId conv, CancellationToken _) =>
            {
                deliveredTo = s;
                delivered = c;
                deliveredConversation = conv;
            })
            .Returns(Task.CompletedTask);

        var notifier = new AgentExchangeProgressNotifier(deliverer.Object, NullLogger<AgentExchangeProgressNotifier>.Instance);
        await notifier.PublishAsync(Event());

        deliveredTo.ShouldBe(InitiatorSession,
            customMessage: "Progress belongs in the INITIATING thread; delivering it to the child " +
                "session would report the handoff to nobody who asked for it.");
        deliveredConversation.ShouldBe(InitiatorConversation);
        delivered.ShouldNotBeNullOrWhiteSpace();
        delivered!.ShouldContain("c_child");
    }

    [Fact]
    public async Task PublishAsync_StampsTheTypedProgressKind_AndTheChildIds()
    {
        var deliverer = new Mock<IOutboundResponseDeliverer>();
        InboundMessage? source = null;
        deliverer
            .Setup(d => d.FanOutAsync(It.IsAny<InboundMessage>(), It.IsAny<SessionId>(), It.IsAny<string?>(), It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .Callback((InboundMessage m, SessionId _, string? _, ConversationId _, CancellationToken _) => source = m)
            .Returns(Task.CompletedTask);

        var notifier = new AgentExchangeProgressNotifier(deliverer.Object, NullLogger<AgentExchangeProgressNotifier>.Instance);
        await notifier.PublishAsync(Event());

        source.ShouldNotBeNull();
        source!.Kind.ShouldBe(MessageKind.AgentExchangeProgress,
            customMessage: "Typed so a renderer can distinguish a handoff status line from a real " +
                "assistant reply without parsing text.");
        source.BindingId.ShouldBeNull(
            customMessage: "Progress did not arrive on a binding, so nothing should be excluded " +
                "from fan-out as an echo source.");
        source.Metadata["childConversationId"].ShouldBe("c_child");
        source.Metadata["childSessionId"].ShouldBe("s_child");
    }

    [Fact]
    public async Task PublishAsync_WhenNoInitiatingSession_DeliversNothing()
    {
        var deliverer = new Mock<IOutboundResponseDeliverer>();
        var notifier = new AgentExchangeProgressNotifier(deliverer.Object, NullLogger<AgentExchangeProgressNotifier>.Instance);

        await notifier.PublishAsync(Event() with { InitiatorSessionId = null });

        deliverer.Verify(
            d => d.FanOutAsync(It.IsAny<InboundMessage>(), It.IsAny<SessionId>(), It.IsAny<string?>(), It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishAsync_WhenFanOutThrows_DoesNotPropagate()
    {
        var deliverer = new Mock<IOutboundResponseDeliverer>();
        deliverer
            .Setup(d => d.FanOutAsync(It.IsAny<InboundMessage>(), It.IsAny<SessionId>(), It.IsAny<string?>(), It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel manager is down"));

        var notifier = new AgentExchangeProgressNotifier(deliverer.Object, NullLogger<AgentExchangeProgressNotifier>.Instance);

        // Should not throw: an unavailable channel must degrade the handoff to silent, never fail it.
        await notifier.PublishAsync(Event());
    }
}
