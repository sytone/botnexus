using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for GatewayHost.FanOutResponseAsync stale-binding self-healing (Issue #130).
/// When a fan-out send throws <see cref="StaleSignalRConnectionException"/>,
/// the binding should be demoted to Muted so future fan-outs skip it.
/// </summary>
public sealed class FanOutStaleBindingTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static Mock<IAgentHandle> CreatePromptHandle(string agentId, string sessionId, string content)
    {
        var h = new Mock<IAgentHandle>();
        h.SetupGet(x => x.AgentId).Returns(agentId);
        h.SetupGet(x => x.SessionId).Returns(sessionId);
        h.Setup(x => x.IsRunning).Returns(false);
        h.Setup(x => x.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = content });
        return h;
    }

    private static InboundMessage CreateMessage(string content, string sessionId = "session-1", string channelType = "web", string conversationId = "conv-1")
        => new()
        {
            ChannelType = channelType,
            SenderId = "sender-1",
            ChannelAddress = conversationId,
            Content = content,
            SessionId = sessionId,
            Metadata = new Dictionary<string, object?>()
        };

    private static GatewayHost CreateHost(
        IAgentSupervisor supervisor,
        IMessageRouter router,
        ISessionStore sessions,
        IChannelManager channelManager,
        IConversationRouter conversationRouter)
        => new(
            supervisor,
            router,
            sessions,
            Mock.Of<IActivityBroadcaster>(),
            channelManager,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationRouter: conversationRouter);

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FanOut_WhenSignalRSendFails_DemotesBindingToMuted()
    {
        // Arrange
        const string agentId = "agent-a";
        const string sessionId = "session-1";
        const string convId = "conv-fanout-stale";
        const string bindingId = "binding-signalr-1";

        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([agentId]);

        var handle = CreatePromptHandle(agentId, sessionId, "hello fan-out");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(AgentId.From(agentId), SessionId.From(sessionId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = ConversationId.From(convId);
        await sessions.SaveAsync(session);

        // Primary (originating) adapter
        var primaryAdapter = new Mock<IChannelAdapter>();
        primaryAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("web"));
        primaryAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // SignalR (fan-out) adapter — throws StaleSignalRConnectionException
        var signalrAdapter = new Mock<IChannelAdapter>();
        signalrAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        signalrAdapter
            .Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StaleSignalRConnectionException(bindingId, convId));

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([primaryAdapter.Object, signalrAdapter.Object]);
        channelManager.Setup(m => m.Get(ChannelKey.From("web"))).Returns(primaryAdapter.Object);
        channelManager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(signalrAdapter.Object);

        // Conversation with a stale signalr binding
        var staleBinding = new ChannelBinding
        {
            BindingId = bindingId,
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = "conn-dead-1",
            Mode = BindingMode.Interactive
        };
        var conversation = new Conversation
        {
            ConversationId = ConversationId.From(convId),
            AgentId = AgentId.From(agentId),
            ChannelBindings = [staleBinding]
        };

        var convRouter = new Mock<IConversationRouter>();
        convRouter
            .Setup(r => r.ResolveInboundAsync(
                It.IsAny<AgentId>(), It.IsAny<ChannelKey>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRoutingResult(conversation, SessionId.From(sessionId), false));

        // Return the stale binding for fan-out
        convRouter
            .Setup(r => r.GetOutboundBindingsAsync(SessionId.From(sessionId), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([staleBinding]);

        convRouter
            .Setup(r => r.MuteBindingByAddressAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId?>(), It.IsAny<ChannelKey>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(supervisor.Object, router.Object, sessions, channelManager.Object, convRouter.Object);

        // Act
        await host.DispatchAsync(CreateMessage("hello", sessionId: sessionId));

        // Assert — binding should have been muted
        convRouter.Verify(r => r.MuteBindingAsync(ConversationId.From(convId), bindingId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FanOut_AfterBindingMuted_SkipsBinding()
    {
        // Arrange: GetOutboundBindingsAsync returns empty list (as it would after muting)
        const string agentId = "agent-a";
        const string sessionId = "session-2";
        const string convId = "conv-fanout-muted";

        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([agentId]);

        var handle = CreatePromptHandle(agentId, sessionId, "reply");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(AgentId.From(agentId), SessionId.From(sessionId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = ConversationId.From(convId);
        await sessions.SaveAsync(session);

        var primaryAdapter = new Mock<IChannelAdapter>();
        primaryAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("web"));
        primaryAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var signalrAdapter = new Mock<IChannelAdapter>();
        signalrAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([primaryAdapter.Object, signalrAdapter.Object]);
        channelManager.Setup(m => m.Get(ChannelKey.From("web"))).Returns(primaryAdapter.Object);
        channelManager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(signalrAdapter.Object);

        var conversation = new Conversation
        {
            ConversationId = ConversationId.From(convId),
            AgentId = AgentId.From(agentId),
        };

        var convRouter = new Mock<IConversationRouter>();
        convRouter
            .Setup(r => r.ResolveInboundAsync(
                It.IsAny<AgentId>(), It.IsAny<ChannelKey>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRoutingResult(conversation, SessionId.From(sessionId), false));

        // Empty list = already muted / no fan-out targets
        convRouter
            .Setup(r => r.GetOutboundBindingsAsync(SessionId.From(sessionId), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await using var host = CreateHost(supervisor.Object, router.Object, sessions, channelManager.Object, convRouter.Object);

        // Act
        await host.DispatchAsync(CreateMessage("ping", sessionId: sessionId));

        // Assert — signalr adapter's SendAsync was never called
        signalrAdapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never,
            "Muted/absent bindings must not receive fan-out");
    }
}
