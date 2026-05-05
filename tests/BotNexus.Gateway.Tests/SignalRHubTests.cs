using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Extensions.Channels.SignalR;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Claims;
using SessionType = BotNexus.Domain.Primitives.SessionType;

namespace BotNexus.Gateway.Tests;

public sealed class SignalRHubTests
{
    [Fact]
    public async Task GatewayHub_OnConnected_SendsConnectionInfo()
    {
        var caller = new Mock<IGatewayHubClient>();
        caller.Setup(proxy => proxy.Connected(It.IsAny<ConnectedPayload>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients<IGatewayHubClient>>();
        clients.SetupGet(value => value.Caller).Returns(caller.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(value => value.GetAll()).Returns([
            new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("assistant"),
                DisplayName = "Assistant",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot"
            }
        ]);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(value => value.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var hub = CreateHub(
            clients: clients.Object,
            activity: activity.Object,
            registry: registry.Object,
            connectionId: "conn-1");

        await hub.OnConnectedAsync();

        caller.Verify(proxy => proxy.Connected(
                It.Is<ConnectedPayload>(p => p.ConnectionId == "conn-1")),
            Times.Once);
        activity.Verify(value => value.PublishAsync(
                It.Is<GatewayActivity>(a =>
                    a.ChannelType == ChannelKey.From("signalr") &&
                    a.Message == "Web Chat client connected."),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_UsesVisibleSessionForAgentChannel()
    {
        // Wave 2: conversation routing creates sessions per conversation binding.
        // When SendMessage is called for agent-a on signalr, a new session is created.
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, connectionId: "conn-1");

        var result = await hub.SendMessage("agent-a", "signalr", "hello");

        result.SessionId.ShouldNotBeNullOrWhiteSpace();
        result.AgentId.ShouldBe("agent-a");
        dispatcher.Verify(value => value.DispatchAsync(
            It.Is<InboundMessage>(m => m.TargetAgentId == "agent-a" && m.Content == "hello"),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_NoVisibleSession_CreatesAndPersistsSession()
    {
        // Wave 2: conversation routing always creates/resolves a session.
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync("conn-1", It.Is<string>(g => g.StartsWith("session:")), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        InboundMessage? dispatched = null;
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched = m)
            .Returns(Task.CompletedTask);

        var hub = CreateHub(groups: groups.Object, dispatcher: dispatcher.Object, connectionId: "conn-1");

        var result = await hub.SendMessage("agent-a", "signalr", "hello");

        result.SessionId.ShouldNotBeNullOrWhiteSpace();
        result.ChannelType.ShouldBe("signalr");
        groups.Verify(value => value.AddToGroupAsync("conn-1", It.Is<string>(g => g.StartsWith("session:")), It.IsAny<CancellationToken>()), Times.Once);
        dispatched.ShouldNotBeNull();
        dispatched!.TargetAgentId.ShouldBe("agent-a");
        dispatched.Content.ShouldBe("hello");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_DispatchesThroughGateway()
    {
        // Wave 2: conversation routing creates a session; dispatcher is called with the correct message.
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, connectionId: "conn-1");

        await hub.SendMessage("agent-a", "signalr", "hello");

        dispatcher.Verify(value => value.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.ChannelType == ChannelKey.From("signalr") &&
                    m.SenderId == "conn-1" &&
                    m.TargetAgentId == "agent-a" &&
                    m.Content == "hello"),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithAgentAndChannelType_RoutesToExistingSession()
    {
        // Wave 2: second message for same agent+channel+address reuses the existing conversation session.
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, connectionId: "conn-1");

        // First message creates the session
        var result1 = await hub.SendMessage("agent-a", "telegram", "first");
        // Second message should reuse the same session (same connection = same channel address)
        var result2 = await hub.SendMessage("agent-a", "telegram", "second");

        result1.SessionId.ShouldNotBeNullOrWhiteSpace();
        result2.SessionId.ShouldBe(result1.SessionId);
    }

    [Fact]
    public async Task SignalR_SameAgent_MultipleConnections_ShareConversation()
    {
        // Two different SignalR connection IDs for the same agent should land
        // in the same conversation because channelAddress = agentId (not connectionId).
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = new DefaultConversationRouter(
            conversationStore,
            sessionStore,
            NullLogger<DefaultConversationRouter>.Instance);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Two hubs with different connection IDs but same underlying stores/router
        var hub1 = new GatewayHub(
            Mock.Of<IAgentSupervisor>(),
            Mock.Of<IAgentRegistry>(),
            sessionStore,
            dispatcher.Object,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<ISessionCompactor>(),
            Mock.Of<ISessionWarmupService>(),
            router,
            conversationStore,
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHub>.Instance)
        {
            Clients = Mock.Of<IHubCallerClients<IGatewayHubClient>>(),
            Groups = Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext("conn-1")
        };

        var hub2 = new GatewayHub(
            Mock.Of<IAgentSupervisor>(),
            Mock.Of<IAgentRegistry>(),
            sessionStore,
            dispatcher.Object,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<ISessionCompactor>(),
            Mock.Of<ISessionWarmupService>(),
            router,
            conversationStore,
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHub>.Instance)
        {
            Clients = Mock.Of<IHubCallerClients<IGatewayHubClient>>(),
            Groups = Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext("conn-2")
        };

        var result1 = await hub1.SendMessage("agent-a", "signalr", "from conn-1");
        var result2 = await hub2.SendMessage("agent-a", "signalr", "from conn-2");

        // Both connections route to the same session (same agent conversation)
        result1.SessionId.ShouldBe(result2.SessionId);
        var conversations = await conversationStore.ListAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"));
        conversations.Count.ShouldBe(1, "two connections for the same agent share one conversation");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithNoSessionForChannel_AutoCreatesSession()
    {
        // Wave 2: sending on a new channel creates a session.
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, connectionId: "conn-1");

        var result = await hub.SendMessage("agent-a", "telegram", "needs-new-session");

        result.SessionId.ShouldNotBeNullOrWhiteSpace();
        result.ChannelType.ShouldBe("telegram");
        dispatcher.Verify(value => value.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.TargetAgentId == "agent-a" &&
                    m.Content == "needs-new-session"),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WhitespaceIds_DispatchesNormalizedIds()
    {
        // Wave 2: whitespace in agentId/channelType is normalized before routing.
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, connectionId: "conn-1");

        await hub.SendMessage("  agent-a  ", "  signalr  ", "hello");

        dispatcher.Verify(value => value.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.ChannelType == ChannelKey.From("signalr") &&
                    m.TargetAgentId == "agent-a"),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_DefaultAgentId_ThrowsHubException()
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.SendMessage(default, ChannelKey.From("signalr"), "hello");

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldBe("Agent ID is required.");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_DefaultChannelType_ThrowsHubException()
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.SendMessage(BotNexus.Domain.Primitives.AgentId.From("agent-a"), default, "hello");

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldBe("Channel type is required.");
    }

    [Fact]
    public async Task ResetSession_ArchivesInsteadOfDeleting()
    {
        var caller = new Mock<IGatewayHubClient>();
        caller.Setup(proxy => proxy.SessionReset(It.IsAny<SessionResetPayload>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients<IGatewayHubClient>>();
        clients.SetupGet(value => value.Caller).Returns(caller.Object);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.ArchiveAsync("session-1", CancellationToken.None)).Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(value => value.StopAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), CancellationToken.None)).Returns(Task.CompletedTask);

        var hub = CreateHub(clients: clients.Object, sessions: sessions.Object, supervisor: supervisor.Object);

        await hub.ResetSession("agent-a", "session-1");

        sessions.Verify(value => value.ArchiveAsync("session-1", CancellationToken.None), Times.Once);
        sessions.Verify(value => value.DeleteAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompactSession_Hub_ReturnsCompactionStats()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetAsync("session-1", CancellationToken.None)).ReturnsAsync(session);
        sessions.Setup(value => value.SaveAsync(session, CancellationToken.None)).Returns(Task.CompletedTask);

        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(value => value.CompactAsync(session.Session, It.IsAny<CompactionOptions>(), CancellationToken.None))
            .ReturnsAsync(new CompactionResult
            {
                Summary = "summary",
                EntriesSummarized = 5,
                EntriesPreserved = 3,
                TokensBefore = 2000,
                TokensAfter = 800
            });

        var hub = CreateHub(
            sessions: sessions.Object,
            compactor: compactor.Object,
            compactionOptions: new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()));

        var result = await hub.CompactSession("agent-a", "session-1");

        result.Summarized.ShouldBe(5);
        result.Preserved.ShouldBe(3);
        result.TokensBefore.ShouldBe(2000);
        result.TokensAfter.ShouldBe(800);
    }

    [Fact]
    public async Task CompactSession_Hub_SessionNotFound_ThrowsHubException()
    {
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetAsync("missing", CancellationToken.None)).ReturnsAsync((GatewaySession?)null);
        var hub = CreateHub(sessions: sessions.Object);

        Func<Task> act = () => hub.CompactSession("agent-a", "missing");

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldBe("Session 'missing' not found.");
    }

    private static GatewayHub CreateHub(
        IHubCallerClients<IGatewayHubClient>? clients = null,
        IGroupManager? groups = null,
        ISessionStore? sessions = null,
        IChannelDispatcher? dispatcher = null,
        IActivityBroadcaster? activity = null,
        IAgentRegistry? registry = null,
        IAgentSupervisor? supervisor = null,
        ISessionCompactor? compactor = null,
        ISessionWarmupService? warmup = null,
        IOptionsMonitor<CompactionOptions>? compactionOptions = null,
        IConversationRouter? conversationRouter = null,
        IConversationStore? conversationStore = null,
        string connectionId = "conn-test")
    {
        var sessionStore = sessions ?? new InMemorySessionStore();
        var convStore = conversationStore ?? new InMemoryConversationStore();
        var router = conversationRouter ?? new DefaultConversationRouter(
            convStore,
            sessionStore,
            NullLogger<DefaultConversationRouter>.Instance);

        var hub = new GatewayHub(
            supervisor ?? Mock.Of<IAgentSupervisor>(),
            registry ?? Mock.Of<IAgentRegistry>(),
            sessionStore,
            dispatcher ?? Mock.Of<IChannelDispatcher>(),
            activity ?? Mock.Of<IActivityBroadcaster>(),
            compactor ?? Mock.Of<ISessionCompactor>(),
            warmup ?? Mock.Of<ISessionWarmupService>(),
            router,
            convStore,
            compactionOptions ?? new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHub>.Instance)
        {
            Clients = clients ?? Mock.Of<IHubCallerClients<IGatewayHubClient>>(),
            Groups = groups ?? Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext(connectionId)
        };

        return hub;
    }

    private sealed class TestHubCallerContext(string connectionId) : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];

        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => "user";
        public override ClaimsPrincipal? User { get; } = new();
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }
    }
}
