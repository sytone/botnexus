using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression coverage for #2631: the gateway selected a single winning channel per turn, so a
/// Service Bus turn emitted no SignalR events (portal went stale) and an agent-initiated
/// <c>internal</c> turn never reached the conversation's Service Bus binding - both completely
/// silently. These tests pin the two directions plus the "silence is the defect" logging rule.
/// </summary>
public sealed class OutboundResponseDelivererSignalRSinkTests
{
    private const string SessionIdStr = "session-2631";
    private const string ConversationIdStr = "conv-2631";

    private static InboundMessage SourceMessage(string channelType = "servicebus") =>
        new()
        {
            ChannelType = ChannelKey.From(channelType),
            SenderId = "user-1",
            Sender = CitizenId.Of(UserId.From("user-1")),
            ChannelAddress = ChannelAddress.From("addr-1"),
            Content = "hi",
            BindingId = BindingId.From("bind-origin"),
            Metadata = new Dictionary<string, object?>()
        };

    private static ChannelBinding Binding(string bindingId, string channelType, string address) =>
        new()
        {
            BindingId = BindingId.From(bindingId),
            ChannelType = ChannelKey.From(channelType),
            ChannelAddress = ChannelAddress.From(address),
            Mode = BindingMode.Interactive
        };

    private static Mock<IChannelAdapter> Adapter(string channelType, List<OutboundMessage>? sink = null)
    {
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From(channelType));
        adapter.SetupGet(a => a.DisplayName).Returns(channelType);
        adapter.SetupGet(a => a.AdapterId).Returns((string?)null);
        if (sink is not null)
        {
            adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
                .Callback<OutboundMessage, CancellationToken>((m, _) => sink.Add(m))
                .Returns(Task.CompletedTask);
        }

        return adapter;
    }

    private static Mock<IChannelManager> ChannelManager(params Mock<IChannelAdapter>[] adapters)
    {
        var mgr = new Mock<IChannelManager>();
        mgr.SetupGet(m => m.Adapters).Returns(adapters.Select(a => a.Object).ToList());
        mgr.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>()))
            .Returns<ChannelKey, string?>((type, _) =>
                adapters.Select(a => a.Object).FirstOrDefault(a => a.ChannelType == type));
        return mgr;
    }

    private static Mock<IConversationRouter> Router(params ChannelBinding[] bindings)
    {
        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bindings.ToList());
        return router;
    }

    private sealed class CapturingLogger : ILogger<OutboundResponseDeliverer>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add((logLevel, formatter(state, exception)));
    }

    // AC5 / Case B: Service Bus-originated turn must still reach the portal.
    [Fact]
    public async Task FanOutAsync_ServiceBusTurnWithNoSignalRBinding_StillDeliversToSignalRSink()
    {
        var signalRSends = new List<OutboundMessage>();
        var signalR = Adapter("signalr", signalRSends);

        var deliverer = new OutboundResponseDeliverer(
            Router().Object, ChannelManager(signalR).Object, new CapturingLogger());

        await deliverer.FanOutAsync(
            SourceMessage("servicebus"), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        signalRSends.ShouldHaveSingleItem();
        signalRSends[0].Content.ShouldBe("reply text");
        signalRSends[0].ConversationId.ShouldBe(ConversationIdStr);
    }

    // AC1: Service Bus delivery and SignalR emission are not mutually exclusive.
    [Fact]
    public async Task FanOutAsync_ServiceBusBinding_DeliversToBothServiceBusAndSignalR()
    {
        var busSends = new List<OutboundMessage>();
        var signalRSends = new List<OutboundMessage>();
        var bus = Adapter("servicebus", busSends);
        var signalR = Adapter("signalr", signalRSends);

        var deliverer = new OutboundResponseDeliverer(
            Router(Binding("bind-bus", "servicebus", "sb-addr")).Object,
            ChannelManager(bus, signalR).Object,
            new CapturingLogger());

        await deliverer.FanOutAsync(
            SourceMessage("telegram"), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        busSends.ShouldHaveSingleItem();
        signalRSends.ShouldHaveSingleItem();
    }

    // AC2 / Case A: agent-initiated (internal) turn reaches external bindings.
    [Fact]
    public async Task FanOutAsync_InternalOriginatedTurn_DeliversToServiceBusBinding()
    {
        var busSends = new List<OutboundMessage>();
        var bus = Adapter("servicebus", busSends);

        var deliverer = new OutboundResponseDeliverer(
            Router(Binding("bind-bus", "servicebus", "sb-addr")).Object,
            ChannelManager(bus).Object,
            new CapturingLogger());

        await deliverer.FanOutAsync(
            SourceMessage("internal"), SessionId.From(SessionIdStr), "alert text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        busSends.ShouldHaveSingleItem();
        busSends[0].Content.ShouldBe("alert text");
        busSends[0].BindingId?.Value.ShouldBe("bind-bus");
    }

    // No double-delivery when the primary path already streamed into SignalR.
    [Fact]
    public async Task FanOutAsync_PrimaryAlreadyDeliveredToSignalR_DoesNotDoubleDeliver()
    {
        var signalRSends = new List<OutboundMessage>();
        var signalR = Adapter("signalr", signalRSends);

        var deliverer = new OutboundResponseDeliverer(
            Router().Object, ChannelManager(signalR).Object, new CapturingLogger());

        await deliverer.FanOutAsync(
            SourceMessage("internal"), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None,
            primaryDeliveredToSignalR: true);

        signalRSends.ShouldBeEmpty();
    }

    // An explicit signalr binding is delivered to exactly once, not twice.
    [Fact]
    public async Task FanOutAsync_ExplicitSignalRBinding_DeliversExactlyOnce()
    {
        var signalRSends = new List<OutboundMessage>();
        var signalR = Adapter("signalr", signalRSends);

        var deliverer = new OutboundResponseDeliverer(
            Router(Binding("bind-sr", "signalr", "conn-1")).Object,
            ChannelManager(signalR).Object,
            new CapturingLogger());

        await deliverer.FanOutAsync(
            SourceMessage("servicebus"), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        signalRSends.ShouldHaveSingleItem();
    }

    // AC3: silence is the defect - a turn with no outbound bindings says so.
    [Fact]
    public async Task FanOutAsync_NoOutboundBindings_LogsExplicitly()
    {
        var log = new CapturingLogger();
        var deliverer = new OutboundResponseDeliverer(
            Router().Object, ChannelManager().Object, log);

        await deliverer.FanOutAsync(
            SourceMessage("servicebus"), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        log.Records.ShouldContain(r => r.Message.Contains("Fan-out: no outbound bindings"));
    }

    // AC3: an undeliverable binding names its id, channel type and reason.
    [Fact]
    public async Task FanOutAsync_UnresolvableAdapter_LogsBindingIdChannelTypeAndReason()
    {
        var log = new CapturingLogger();
        var deliverer = new OutboundResponseDeliverer(
            Router(Binding("bind-ghost", "webhook", "hook-addr")).Object,
            ChannelManager().Object,
            log);

        await deliverer.FanOutAsync(
            SourceMessage("servicebus"), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        log.Records.ShouldContain(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains("bind-ghost") &&
            r.Message.Contains("webhook") &&
            r.Message.Contains("Skipping"));
    }

    // AC3: the SignalR sink itself logs a skip when no adapter is registered.
    [Fact]
    public async Task FanOutAsync_NoSignalRAdapterRegistered_LogsSinkSkip()
    {
        var log = new CapturingLogger();
        var bus = Adapter("servicebus", []);

        var deliverer = new OutboundResponseDeliverer(
            Router(Binding("bind-bus", "servicebus", "sb-addr")).Object,
            ChannelManager(bus).Object,
            log);

        await deliverer.FanOutAsync(
            SourceMessage("telegram"), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        log.Records.ShouldContain(r => r.Message.Contains("Fan-out: SignalR sink"));
    }

    // The SignalR sink never blocks or fails the turn.
    [Fact]
    public async Task FanOutAsync_SignalRSinkThrows_IsSwallowed()
    {
        var signalR = Adapter("signalr");
        signalR.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub gone"));

        var log = new CapturingLogger();
        var deliverer = new OutboundResponseDeliverer(
            Router().Object, ChannelManager(signalR).Object, log);

        await deliverer.FanOutAsync(
            SourceMessage("servicebus"), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        log.Records.ShouldContain(r => r.Level == LogLevel.Warning && r.Message.Contains("SignalR sink"));
    }
}
