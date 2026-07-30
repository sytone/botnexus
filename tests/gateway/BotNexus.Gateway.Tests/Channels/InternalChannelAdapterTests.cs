using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Channels;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Moq;

using BotNexus.Gateway.Tests;

namespace BotNexus.Gateway.Tests.Channels;

public sealed class InternalChannelAdapterTests
{
    [Fact]
    public async Task SendAsync_WithSessionHavingSignalrChannel_DelegatesToSignalr()
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = SessionId.From("session-1"),
                AgentId = AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("signalr")
            });

        var signalrAdapter = new Mock<IChannelAdapter>();
        signalrAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        signalrAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(signalrAdapter.Object);

        var sut = CreateAdapter(channelManager.Object, sessionStore.Object);
        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("internal"),
            ChannelAddress = ChannelAddress.From("conversation-1"),
            SessionId = "session-1",
            Content = "hello"
        };

        await sut.SendAsync(outbound, CancellationToken.None);

        signalrAdapter.Verify(
            a => a.SendAsync(
                It.Is<OutboundMessage>(m =>
                    m.ChannelType.Equals(ChannelKey.From("signalr")) &&
                    m.ChannelAddress == ChannelAddress.From("conversation-1") &&
                    m.SessionId == "session-1" &&
                    m.Content == "hello"),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_WithNoSession_FallsBackToSignalr()
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GatewaySession?)null);

        var signalrAdapter = new Mock<IChannelAdapter>();
        signalrAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        signalrAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(signalrAdapter.Object);

        var sut = CreateAdapter(channelManager.Object, sessionStore.Object);

        await sut.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("internal"),
            ChannelAddress = ChannelAddress.From("conversation-1"),
            SessionId = "session-1",
            Content = "hello"
        }, CancellationToken.None);

        signalrAdapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task SendAsync_WithNoAdaptersAvailable_LogsWarningAndReturns()
    {
        var logger = new Mock<ILogger<InternalChannelAdapter>>();
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GatewaySession?)null);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((IChannelAdapter?)null);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IChannelManager))).Returns(channelManager.Object);

        var sut = new InternalChannelAdapter(serviceProvider.Object, sessionStore.Object, logger.Object);

        Func<Task> act = () => sut.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("internal"),
            ChannelAddress = ChannelAddress.From("conversation-1"),
            SessionId = "session-1",
            Content = "hello"
        }, CancellationToken.None);

        await act.ShouldNotThrowAsync();

        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("no target channel resolved", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_DoesNotDelegateToSelf()
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = SessionId.From("session-1"),
                AgentId = AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("internal")
            });

        var selfAdapter = new Mock<IChannelAdapter>();
        selfAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("internal"));

        var signalrAdapter = new Mock<IChannelAdapter>();
        signalrAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        signalrAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("internal"))).Returns(selfAdapter.Object);
        channelManager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(signalrAdapter.Object);

        var sut = CreateAdapter(channelManager.Object, sessionStore.Object);

        await sut.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("internal"),
            ChannelAddress = ChannelAddress.From("conversation-1"),
            SessionId = "session-1",
            Content = "hello"
        }, CancellationToken.None);

        selfAdapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        signalrAdapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task SendStreamDeltaAsync_DelegatesToResolvedChannel()
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = SessionId.From("session-1"),
                AgentId = AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("signalr")
            });

        var signalrAdapter = new Mock<IChannelAdapter>();
        signalrAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        signalrAdapter.Setup(a => a.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(signalrAdapter.Object);

        var sut = CreateAdapter(channelManager.Object, sessionStore.Object);

        await sut.SendStreamDeltaAsync(StreamTargets.For("session-1"), "delta-text", CancellationToken.None);

        signalrAdapter.Verify(a => a.SendStreamDeltaAsync(StreamTargets.For("session-1"), "delta-text", CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task SendStreamEventAsync_ResolvesByTypedSessionId_AndRoutesToParentsNonSignalRChannel()
    {
        // Behaviour fix introduced with ChannelStreamTarget (#677): the resolver now uses the
        // typed SessionId from the stream target instead of trying to parse the previous
        // string "conversationId" parameter (which was actually a ChannelAddress). The old code
        // silently fell through to the SignalR fallback for any non-SignalR parent session,
        // so a sub-agent wake-up event for a Telegram-rooted session would have been
        // misdelivered to SignalR. This test pins the correct typed-resolution behaviour.
        var parentSession = SessionId.From("parent-session-telegram");
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(parentSession, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = parentSession,
                AgentId = AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("telegram")
            });

        var telegramAdapter = new Mock<IChannelAdapter>();
        telegramAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("telegram"));
        var telegramStream = telegramAdapter.As<IStreamEventChannelAdapter>();
        telegramStream.Setup(a => a.CanSendStreamEvent(It.IsAny<ChannelStreamTarget>())).Returns(true);
        telegramStream.Setup(a => a.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // SignalR fallback adapter — it must NOT be called for a Telegram-rooted parent session.
        var signalrAdapter = new Mock<IChannelAdapter>();
        signalrAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        var signalrStream = signalrAdapter.As<IStreamEventChannelAdapter>();
        signalrStream.Setup(a => a.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("telegram"))).Returns(telegramAdapter.Object);
        channelManager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(signalrAdapter.Object);

        var sut = CreateAdapter(channelManager.Object, sessionStore.Object);
        var target = new ChannelStreamTarget(
            ConversationId.From("conv-parent-1"),
            parentSession,
            ChannelAddress.From("telegram-chat-99"),
            null);
        var wakeEvent = new AgentStreamEvent { Type = AgentStreamEventType.MessageStart };

        await sut.SendStreamEventAsync(target, wakeEvent, CancellationToken.None);

        telegramStream.Verify(
            a => a.SendStreamEventAsync(target, wakeEvent, CancellationToken.None),
            Times.Once,
            "wake-up stream event must be delivered to the parent's true channel (Telegram), not the SignalR fallback");
        signalrStream.Verify(
            a => a.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "SignalR fallback must not receive the event when the parent session has a non-SignalR channel");
    }

    [Fact]
    public async Task SendStreamEventAsync_TargetAdapterCannotSatisfyPrecondition_SkipsDeliveryWithoutThrowing()
    {
        // #2559: the internal adapter is a fan-out hop. A resolvable but unsatisfiable target
        // (Service Bus with no ChannelRequestId) must be skipped exactly like an unresolvable
        // one, instead of letting the target's precondition exception kill the agent turn.
        var session = SessionId.From("session-sb");
        var target = new ChannelStreamTarget(
            ConversationId.From("conv-sb"),
            session,
            ChannelAddress.From("sb-address"),
            null);
        var streamEvent = new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hi" };

        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(x => x.GetAsync(session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = session,
                AgentId = AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("servicebus")
            });

        var sbAdapter = new Mock<IChannelAdapter>();
        sbAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("servicebus"));
        var sbStream = sbAdapter.As<IStreamEventChannelAdapter>();
        sbStream.Setup(a => a.CanSendStreamEvent(It.IsAny<ChannelStreamTarget>())).Returns(false);
        sbStream.Setup(a => a.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("A Service Bus stream requires a channel request identity."));

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("servicebus"))).Returns(sbAdapter.Object);

        var sut = CreateAdapter(channelManager.Object, sessionStore.Object);

        Func<Task> act = () => sut.SendStreamEventAsync(target, streamEvent, CancellationToken.None);

        await act.ShouldNotThrowAsync();
        sbStream.Verify(
            a => a.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an unsatisfiable target must never be invoked");
        sbAdapter.Verify(
            a => a.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the skip must not silently reroute through the plain delta path");
    }

    [Fact]
    public async Task SendStreamEventAsync_TargetAdapterCannotSatisfyPrecondition_LogsSingleWarningNamingSessionAndChannelType()
    {
        var session = SessionId.From("session-sb-log");
        var target = new ChannelStreamTarget(
            ConversationId.From("conv-sb"),
            session,
            ChannelAddress.From("sb-address"),
            null);

        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(x => x.GetAsync(session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = session,
                AgentId = AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("servicebus")
            });

        var sbAdapter = new Mock<IChannelAdapter>();
        sbAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("servicebus"));
        var sbStream = sbAdapter.As<IStreamEventChannelAdapter>();
        sbStream.Setup(a => a.CanSendStreamEvent(It.IsAny<ChannelStreamTarget>())).Returns(false);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("servicebus"))).Returns(sbAdapter.Object);

        var logger = new Mock<ILogger<InternalChannelAdapter>>();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IChannelManager))).Returns(channelManager.Object);
        var sut = new InternalChannelAdapter(serviceProvider.Object, sessionStore.Object, logger.Object);

        await sut.SendStreamEventAsync(
            target,
            new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hi" },
            CancellationToken.None);

        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("session-sb-log", StringComparison.Ordinal) &&
                    v.ToString()!.Contains("servicebus", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "the undeliverable stream event must be reported once at Warning, naming the session and the target channel type");
    }

    [Fact]
    public async Task SendStreamEventAsync_TargetAdapterSatisfiesPrecondition_DeliversEvent()
    {
        var session = SessionId.From("session-sb-ok");
        var target = new ChannelStreamTarget(
            ConversationId.From("conv-sb"),
            session,
            ChannelAddress.From("sb-address"),
            null,
            ChannelRequestId: "request-1");
        var streamEvent = new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hi" };

        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(x => x.GetAsync(session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = session,
                AgentId = AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("servicebus")
            });

        var sbAdapter = new Mock<IChannelAdapter>();
        sbAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("servicebus"));
        var sbStream = sbAdapter.As<IStreamEventChannelAdapter>();
        sbStream.Setup(a => a.CanSendStreamEvent(It.IsAny<ChannelStreamTarget>())).Returns(true);
        sbStream.Setup(a => a.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("servicebus"))).Returns(sbAdapter.Object);

        var sut = CreateAdapter(channelManager.Object, sessionStore.Object);

        await sut.SendStreamEventAsync(target, streamEvent, CancellationToken.None);

        sbStream.Verify(
            a => a.SendStreamEventAsync(target, streamEvent, CancellationToken.None),
            Times.Once,
            "a satisfiable Service Bus target must still stream exactly as before");
    }

    [Fact]
    public void ChannelType_ReturnsInternal()
    {
        var sut = CreateAdapter(new Mock<IChannelManager>().Object, new Mock<ISessionStore>().Object);

        sut.ChannelType.Value.ShouldBe("internal");
    }

    private static InternalChannelAdapter CreateAdapter(IChannelManager channelManager, ISessionStore sessionStore)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IChannelManager))).Returns(channelManager);

        return new InternalChannelAdapter(
            serviceProvider.Object,
            sessionStore,
            Mock.Of<ILogger<InternalChannelAdapter>>());
    }
}
