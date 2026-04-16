using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Channels;
using BotNexus.Domain.Primitives;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

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
            ConversationId = "conversation-1",
            SessionId = "session-1",
            Content = "hello"
        };

        await sut.SendAsync(outbound, CancellationToken.None);

        signalrAdapter.Verify(
            a => a.SendAsync(
                It.Is<OutboundMessage>(m =>
                    m.ChannelType.Equals(ChannelKey.From("signalr")) &&
                    m.ConversationId == "conversation-1" &&
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
            ConversationId = "conversation-1",
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
            ConversationId = "conversation-1",
            SessionId = "session-1",
            Content = "hello"
        }, CancellationToken.None);

        await act.Should().NotThrowAsync();

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
            ConversationId = "conversation-1",
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
        signalrAdapter.Setup(a => a.SendStreamDeltaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(signalrAdapter.Object);

        var sut = CreateAdapter(channelManager.Object, sessionStore.Object);

        await sut.SendStreamDeltaAsync("session-1", "delta-text", CancellationToken.None);

        signalrAdapter.Verify(a => a.SendStreamDeltaAsync("session-1", "delta-text", CancellationToken.None), Times.Once);
    }

    [Fact]
    public void ChannelType_ReturnsInternal()
    {
        var sut = CreateAdapter(new Mock<IChannelManager>().Object, new Mock<ISessionStore>().Object);

        sut.ChannelType.Value.Should().Be("internal");
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
