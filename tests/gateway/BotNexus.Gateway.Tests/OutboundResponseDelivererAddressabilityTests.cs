using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3518: the fan-out must not build an envelope for a binding whose adapter has already said it
/// cannot address it.
/// <para>
/// PR #3418 (for #3181) started stamping the INTERNAL <c>c_&lt;guid&gt;</c> conversation id onto every
/// fan-out envelope. On Service Bus that value is a gateway routing key, never an external wire
/// address, so the fail-closed #2815 guard threw once per turn and the reply was silently lost -
/// 31 refused envelopes in a 24h window. The remedy is NOT to relax the guard (that reintroduces
/// the #2529 cross-conversation misdelivery) but to ask the adapter first, via
/// <see cref="IAddressableChannelAdapter"/>, and skip an unaddressable binding.
/// </para>
/// </summary>
public sealed class OutboundResponseDelivererAddressabilityTests
{
    private const string SessionIdStr = "session-3518";
    private const string InternalConversationId = "c_e605a3784017447da1c26000cf32edce";

    /// <summary>Adapter double implementing BOTH contracts; Moq cannot mock two interfaces on one object here.</summary>
    private sealed class AddressableAdapterStub(string channelType, bool canDeliver) : IChannelAdapter, IAddressableChannelAdapter
    {
        public ChannelKey ChannelType { get; } = ChannelKey.From(channelType);
        public string DisplayName => ChannelType.Value;
        public bool SupportsStreaming => false;
        public bool SupportsSteering => false;
        public bool SupportsFollowUp => false;
        public bool SupportsThinkingDisplay => false;
        public bool SupportsToolDisplay => false;
        public bool SupportsInboundImages => false;
        public bool IsRunning => true;

        public List<OutboundMessage> Sent { get; } = [];
        public List<ChannelAddress> Probed { get; } = [];

        public bool CanDeliverTo(ChannelAddress channelAddress, out string? reason)
        {
            Probed.Add(channelAddress);
            reason = canDeliver ? null : "test: no external destination";
            return canDeliver;
        }

        public Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static InboundMessage SourceMessage() => new()
    {
        ChannelType = ChannelKey.From("telegram"),
        SenderId = "user-1",
        Sender = CitizenId.Of(UserId.From("user-1")),
        ChannelAddress = ChannelAddress.From("chat-1"),
        Content = "hi",
        BindingId = BindingId.From("bind-origin"),
        Metadata = new Dictionary<string, object?>()
    };

    private static ChannelBinding Binding(string address) => new()
    {
        BindingId = BindingId.From("bind-sb"),
        ChannelType = ChannelKey.From("servicebus"),
        ChannelAddress = ChannelAddress.From(address),
        Mode = BindingMode.Interactive
    };

    private static Mock<IChannelManager> ChannelManager(IChannelAdapter adapter)
    {
        var mgr = new Mock<IChannelManager>();
        mgr.SetupGet(m => m.Adapters).Returns([adapter]);
        mgr.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>()))
            .Returns<ChannelKey, string?>((type, _) => type == adapter.ChannelType ? adapter : null);
        return mgr;
    }

    private static Mock<IConversationRouter> RouterReturning(ChannelBinding binding)
    {
        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([binding]);
        return router;
    }

    private static async Task<AddressableAdapterStub> FanOutTo(bool canDeliver)
    {
        var adapter = new AddressableAdapterStub("servicebus", canDeliver);
        var deliverer = new OutboundResponseDeliverer(
            RouterReturning(Binding("keel")).Object,
            ChannelManager(adapter).Object,
            NullLogger<OutboundResponseDeliverer>.Instance);

        await deliverer.FanOutAsync(
            SourceMessage(),
            SessionId.From(SessionIdStr),
            "reply text",
            ConversationId.From(InternalConversationId),
            CancellationToken.None);

        return adapter;
    }

    /// <summary>
    /// AC2: the binding the refusals were logged against - a gateway-created Service Bus binding
    /// addressed by AGENT ID. When the adapter reports it unaddressable, NO envelope is built.
    /// Non-vacuity: delete the probe in <c>DeliverToBindingAsync</c> and this reddens, because the
    /// deliverer goes straight back to sending the internal id the guard must refuse.
    /// </summary>
    [Fact]
    public async Task FanOutAsync_AdapterReportsAddressUndeliverable_SendsNothing()
    {
        var adapter = await FanOutTo(canDeliver: false);

        adapter.Probed.ShouldHaveSingleItem().Value.ShouldBe("keel");
        adapter.Sent.ShouldBeEmpty(
            "an unaddressable binding must be skipped at the deliverer, not refused one layer down");
    }

    /// <summary>
    /// The converse clause, so the skip cannot be satisfied by simply never delivering: an adapter
    /// that reports the address deliverable still receives the envelope, conversation id intact.
    /// </summary>
    [Fact]
    public async Task FanOutAsync_AdapterReportsAddressDeliverable_StillSends()
    {
        var adapter = await FanOutTo(canDeliver: true);

        adapter.Probed.ShouldHaveSingleItem();
        var sent = adapter.Sent.ShouldHaveSingleItem();
        sent.ConversationId.ShouldBe(InternalConversationId);
        sent.ChannelAddress.Value.ShouldBe("keel");
    }

    /// <summary>
    /// Back-compat: an adapter that does NOT implement <see cref="IAddressableChannelAdapter"/> is
    /// treated as always addressable, so the probe cannot silently suppress existing channels.
    /// </summary>
    [Fact]
    public async Task FanOutAsync_AdapterWithoutAddressabilityProbe_StillSends()
    {
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("telegram"));
        adapter.SetupGet(a => a.DisplayName).Returns("telegram");
        adapter.SetupGet(a => a.AdapterId).Returns((string?)null);
        var sent = new List<OutboundMessage>();
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => sent.Add(m))
            .Returns(Task.CompletedTask);

        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("bind-tg"),
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("chat-9"),
            Mode = BindingMode.Interactive
        };

        var deliverer = new OutboundResponseDeliverer(
            RouterReturning(binding).Object,
            ChannelManager(adapter.Object).Object,
            NullLogger<OutboundResponseDeliverer>.Instance);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(InternalConversationId), CancellationToken.None);

        sent.ShouldHaveSingleItem().ChannelAddress.Value.ShouldBe("chat-9");
    }
}
