using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.World;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="OutboundResponseDeliverer"/>, the outbound fan-out delivery collaborator
/// extracted from <see cref="GatewayHost"/> (#1811). These drive the deliverer directly against mock
/// <see cref="IChannelManager"/> / <see cref="IConversationRouter"/> collaborators - the behaviour that
/// previously could only be reached through the full 24-dependency inbound turn pipeline. Covers the
/// five acceptance-criteria cases: fan-out to N bindings, non-deliverable channel skip, adapter-not-found
/// skip, stale-connection demote-to-Muted, and generic send-failure swallow.
/// </summary>
public sealed class OutboundResponseDelivererTests
{
    private const string SessionIdStr = "session-fanout-1";
    private const string ConversationIdStr = "conv-fanout-1";

    private static InboundMessage SourceMessage(string? originatingBindingId = "bind-origin") =>
        new()
        {
            ChannelType = ChannelKey.From("telegram"),
            SenderId = "user-1",
            Sender = CitizenId.Of(UserId.From("user-1")),
            ChannelAddress = ChannelAddress.From("chat-1"),
            Content = "hi",
            BindingId = originatingBindingId is null ? null : BindingId.From(originatingBindingId),
            Metadata = new Dictionary<string, object?>()
        };

    private static ChannelBinding Binding(string bindingId, string channelType, string address, string? prefix = null) =>
        new()
        {
            BindingId = BindingId.From(bindingId),
            ChannelType = ChannelKey.From(channelType),
            ChannelAddress = ChannelAddress.From(address),
            DisplayPrefix = prefix,
            Mode = BindingMode.Interactive
        };

    private static Mock<IChannelAdapter> Adapter(string channelType)
    {
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From(channelType));
        adapter.SetupGet(a => a.DisplayName).Returns(channelType);
        adapter.SetupGet(a => a.AdapterId).Returns((string?)null);
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

    private static OutboundResponseDeliverer CreateDeliverer(IConversationRouter router, IChannelManager channelManager) =>
        new(router, channelManager, NullLogger<OutboundResponseDeliverer>.Instance);

    // ── AC 1: successful fan-out to N bindings ────────────────────────────────
    [Fact]
    public async Task FanOutAsync_DeliversToAllBindings()
    {
        var bindingA = Binding("bind-a", "telegram", "chat-a", "[A]");
        var bindingB = Binding("bind-b", "signal", "chat-b", "[B]");

        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([bindingA, bindingB]);

        var adapterA = Adapter("telegram");
        var adapterB = Adapter("signal");
        var sentA = new List<OutboundMessage>();
        var sentB = new List<OutboundMessage>();
        adapterA.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => sentA.Add(m)).Returns(Task.CompletedTask);
        adapterB.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => sentB.Add(m)).Returns(Task.CompletedTask);

        var deliverer = CreateDeliverer(router.Object, ChannelManager(adapterA, adapterB).Object);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        sentA.ShouldHaveSingleItem();
        sentB.ShouldHaveSingleItem();
        sentA[0].Content.ShouldBe("reply text");
        sentA[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-a"));
        sentA[0].BindingId?.Value.ShouldBe("bind-a");
        sentA[0].DisplayPrefix.ShouldBe("[A]");
        sentB[0].BindingId?.Value.ShouldBe("bind-b");
        sentB[0].DisplayPrefix.ShouldBe("[B]");
    }

    [Fact]
    public async Task FanOutAsync_NullOrEmptyContent_DeliversNothing()
    {
        var router = new Mock<IConversationRouter>();
        var deliverer = CreateDeliverer(router.Object, ChannelManager().Object);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        router.Verify(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── AC 2: non-deliverable channel skip ────────────────────────────────────
    [Fact]
    public async Task FanOutAsync_NonDeliverableChannel_Skipped()
    {
        var cronBinding = Binding("bind-cron", "cron", "cron-addr");

        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([cronBinding]);

        // No adapter registered - manager.Get would return null, but the non-deliverable skip
        // must short-circuit before adapter resolution is even attempted.
        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([]);

        var deliverer = CreateDeliverer(router.Object, channelManager.Object);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        channelManager.Verify(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>()), Times.Never);
    }

    // ── AC 3: adapter-not-found skip ──────────────────────────────────────────
    [Fact]
    public async Task FanOutAsync_AdapterNotFound_SkippedWithoutThrowing()
    {
        var binding = Binding("bind-x", "telegram", "chat-x");

        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([binding]);

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([]);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((IChannelAdapter?)null);

        var deliverer = CreateDeliverer(router.Object, channelManager.Object);

        // Must not throw and must not attempt mute (adapter-not-found is not a stale connection).
        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        router.Verify(r => r.MuteBindingAsync(It.IsAny<ConversationId>(), It.IsAny<BindingId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── AC 4: stale-connection demote-to-Muted ────────────────────────────────
    [Fact]
    public async Task FanOutAsync_StaleConnection_DemotesBindingToMuted()
    {
        var binding = Binding("bind-stale", "signalr", "conn-123");

        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([binding]);
        router.Setup(r => r.MuteBindingAsync(It.IsAny<ConversationId>(), It.IsAny<BindingId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var adapter = Adapter("signalr");
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StaleChannelConnectionException(
                BindingId.From("bind-stale"), ConversationId.From(ConversationIdStr)));

        var deliverer = CreateDeliverer(router.Object, ChannelManager(adapter).Object);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        router.Verify(r => r.MuteBindingAsync(
            ConversationId.From(ConversationIdStr), BindingId.From("bind-stale"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── AC 5: generic send-failure swallow ────────────────────────────────────
    [Fact]
    public async Task FanOutAsync_GenericSendFailure_SwallowedAndContinues()
    {
        var failing = Binding("bind-fail", "telegram", "chat-fail");
        var healthy = Binding("bind-ok", "signal", "chat-ok");

        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([failing, healthy]);

        var failingAdapter = Adapter("telegram");
        failingAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var healthyAdapter = Adapter("signal");
        var healthySends = new List<OutboundMessage>();
        healthyAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => healthySends.Add(m)).Returns(Task.CompletedTask);

        var deliverer = CreateDeliverer(router.Object, ChannelManager(failingAdapter, healthyAdapter).Object);

        // The generic failure on the first binding must be swallowed so the second still delivers.
        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        healthySends.ShouldHaveSingleItem();
        healthySends[0].BindingId?.Value.ShouldBe("bind-ok");
        router.Verify(r => r.MuteBindingAsync(It.IsAny<ConversationId>(), It.IsAny<BindingId>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
