using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Dispatching;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Services;
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
                Emoji = "✨",
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
                It.Is<ConnectedPayload>(p => p.ConnectionId == "conn-1" &&
                    p.Agents.Single().Emoji == "✨")),
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
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1");

        var result = await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        result.SessionId.ShouldNotBeNullOrWhiteSpace();
        result.AgentId.ShouldBe("agent-a");
        var captured = orchestrator.Captured.ShouldHaveSingleItem();
        captured.RoutingHints.ShouldNotBeNull();
        captured.RoutingHints!.RequestedAgentId.ShouldNotBeNull();
        captured.RoutingHints.RequestedAgentId!.Value.Value.ShouldBe("agent-a");
        captured.Content.ShouldBe("hello");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_NoVisibleSession_CreatesAndPersistsSession()
    {
        // Wave 2: conversation routing always creates/resolves a session.
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync("conn-1", It.Is<string>(g => g.StartsWith("conversation:")), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(groups: groups.Object, orchestrator: orchestrator, connectionId: "conn-1");

        var result = await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        result.SessionId.ShouldNotBeNullOrWhiteSpace();
        result.ChannelType.ShouldBe("signalr");
        // PR1.5 (#682): connection now subscribes to the conversation group, not the session group.
        groups.Verify(value => value.AddToGroupAsync("conn-1", It.Is<string>(g => g.StartsWith("conversation:")), It.IsAny<CancellationToken>()), Times.Once);
        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.RoutingHints.ShouldNotBeNull();
        dispatched.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("agent-a");
        dispatched.Content.ShouldBe("hello");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_DispatchesThroughGateway()
    {
        // Wave 2: conversation routing creates a session; orchestrator is called with the correct message.
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1");

        await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.ChannelType.ShouldBe(ChannelKey.From("signalr"));
        dispatched.SenderId.ShouldBe("conn-1");
        dispatched.RoutingHints.ShouldNotBeNull();
        dispatched.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("agent-a");
        dispatched.Content.ShouldBe("hello");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithAgentAndChannelType_RoutesToExistingSession()
    {
        // Wave 2: second message for same agent+channel+address reuses the existing conversation session.
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1");

        // First message creates the session
        var result1 = await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("telegram"), "first");
        // Second message should reuse the same session (same connection = same channel address)
        var result2 = await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("telegram"), "second");

        result1.SessionId.ShouldNotBeNullOrWhiteSpace();
        result2.SessionId.ShouldBe(result1.SessionId);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithConversationId_ResolvesConversationSession()
    {
        const string defaultSessionId = "session-default";
        const string targetSessionId = "session-target";
        const string targetConversationId = "conv-target";

        var orchestrator = new CapturingInboundMessageOrchestrator();

        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher.Setup(value => value.DispatchAsync(
                It.Is<InboundMessageContext>(context => context.RequestedConversationId == BotNexus.Domain.Primitives.ConversationId.From(targetConversationId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(
                    BotNexus.Domain.Primitives.ConversationId.From(targetConversationId),
                    BotNexus.Domain.Primitives.SessionId.From(targetSessionId),
                    false,
                    false)));

        conversationDispatcher.Setup(value => value.DispatchAsync(
                It.Is<InboundMessageContext>(context => context.RequestedConversationId == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(
                    BotNexus.Domain.Primitives.ConversationId.From("conv-default"),
                    BotNexus.Domain.Primitives.SessionId.From(defaultSessionId),
                    false,
                    false)));

        var hub = CreateHub(orchestrator: orchestrator, conversationDispatcher: conversationDispatcher.Object, connectionId: "conn-1");

        var result = await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello targeted", targetConversationId);

        result.SessionId.ShouldBe(targetSessionId,
            "explicit conversation routing should resolve the conversation session instead of the default portal session");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithoutConversationId_UsesDefaultConversationSession()
    {
        const string defaultSessionId = "session-default";

        var orchestrator = new CapturingInboundMessageOrchestrator();

        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher.Setup(value => value.DispatchAsync(
                It.Is<InboundMessageContext>(context => context.RequestedConversationId == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(
                    BotNexus.Domain.Primitives.ConversationId.From("conv-default"),
                    BotNexus.Domain.Primitives.SessionId.From(defaultSessionId),
                    false,
                    false)));

        var hub = CreateHub(orchestrator: orchestrator, conversationDispatcher: conversationDispatcher.Object, connectionId: "conn-1");

        var result = await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello default");

        result.SessionId.ShouldBe(defaultSessionId,
            "when no conversationId is supplied, hub routing should still return the default conversation session");
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

        var orchestrator = new CapturingInboundMessageOrchestrator();

        var conversationDispatcher = new DefaultConversationDispatcher(router, conversationStore);

        static SessionCompactionCoordinator NewCoordinator(ISessionStore store)
            => new(
                Mock.Of<ISessionCompactor>(),
                store,
                Mock.Of<IAgentSupervisor>(),
                Mock.Of<IChannelManager>(),
                new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
                NullLogger<SessionCompactionCoordinator>.Instance);

        // Two hubs with different connection IDs but same underlying stores/router
        var hub1 = new GatewayHub(
            Mock.Of<IAgentSupervisor>(),
            Mock.Of<IAgentRegistry>(),
            sessionStore,
            orchestrator,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<ISessionCompactor>(),
            Mock.Of<ISessionWarmupService>(),
            conversationDispatcher,
            router,
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHub>.Instance,
            compactionCoordinator: NewCoordinator(sessionStore))
        {
            Clients = Mock.Of<IHubCallerClients<IGatewayHubClient>>(),
            Groups = Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext("conn-1")
        };

        var hub2 = new GatewayHub(
            Mock.Of<IAgentSupervisor>(),
            Mock.Of<IAgentRegistry>(),
            sessionStore,
            orchestrator,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<ISessionCompactor>(),
            Mock.Of<ISessionWarmupService>(),
            conversationDispatcher,
            router,
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHub>.Instance,
            compactionCoordinator: NewCoordinator(sessionStore))
        {
            Clients = Mock.Of<IHubCallerClients<IGatewayHubClient>>(),
            Groups = Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext("conn-2")
        };

        var result1 = await hub1.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "from conn-1");
        var result2 = await hub2.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "from conn-2");

        // Both connections route to the same session (same agent conversation)
        result1.SessionId.ShouldBe(result2.SessionId);
        var conversations = await conversationStore.ListAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"));
        conversations.Count.ShouldBe(1, "two connections for the same agent share one conversation");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithNoSessionForChannel_AutoCreatesSession()
    {
        // Wave 2: sending on a new channel creates a session.
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1");

        var result = await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("telegram"), "needs-new-session");

        result.SessionId.ShouldNotBeNullOrWhiteSpace();
        result.ChannelType.ShouldBe("telegram");
        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.RoutingHints.ShouldNotBeNull();
        dispatched.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("agent-a");
        dispatched.Content.ShouldBe("needs-new-session");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WhitespaceIds_DispatchesNormalizedIds()
    {
        // Wave 2: whitespace in agentId/channelType is normalized before routing.
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1");

        await hub.SendMessage(AgentId.From("  agent-a  "), ChannelKey.From("  signalr  "), "hello");

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.ChannelType.ShouldBe(ChannelKey.From("signalr"));
        dispatched.RoutingHints.ShouldNotBeNull();
        dispatched.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("agent-a");
    }

    [Fact]
    public async Task GatewayHub_RespondToAskUser_CompletesPendingResponse()
    {
        var registry = new AskUserResponseRegistry();
        var conversationStore = new InMemoryConversationStore();
        var conversation = await conversationStore.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("conversation-respond"),
            AgentId = AgentId.From("agent-a"),
            Title = "ask user",
            ChannelBindings =
            [
                new ChannelBinding
                {
                    ChannelType = ChannelKey.From("signalr"),
                    ChannelAddress = ChannelAddress.From("agent-a")
                }
            ]
        });

        var (requestId, task) = registry.Register(conversation.ConversationId, TimeSpan.FromMinutes(1));
        var hub = CreateHub(conversationStore: conversationStore, askUserResponseRegistry: registry);

        await hub.RespondToAskUser(conversation.ConversationId.Value, requestId, "staging", null, cancelled: false);

        var response = await task;
        response.RequestId.ShouldBe(requestId);
        response.FreeFormText.ShouldBe("staging");
    }

    // GatewayHub_SendMessage_DefaultAgentId_ThrowsHubException was removed: AgentId is now a Vogen
    // value object and `default(AgentId)` is rejected by the analyser (VOG009). The hub method
    // signature `SendMessage(AgentId agentId, ...)` cannot receive a default instance from any
    // code path. Construction-time validation is asserted in AgentIdTests.From_RejectsNullEmptyOrWhitespace.

    [Fact]
    public async Task GatewayHub_SendMessage_DefaultChannelType_ThrowsHubException()
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.SendMessage(BotNexus.Domain.Primitives.AgentId.From("agent-a"), default, "hello");

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldBe("Channel type is required.");
    }

    [Fact]
    public async Task GatewayHub_Steer_UsesRequestedSessionId()
    {
        const string requestedSessionId = "session-steer-target";

        var handle = Mock.Of<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);

        var hub = CreateHub(supervisor: supervisor.Object, connectionId: "conn-1");

        var result = await hub.Steer(AgentId.From("agent-a"), SessionId.From(requestedSessionId), "nudge", null);

        result.SessionId.ShouldBe(requestedSessionId);
        // Verify SteerAsync was called on the handle with the content
        Mock.Get(handle).Verify(h => h.SteerAsync("nudge", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GatewayHub_Steer_SetsConversationIdOnDispatchedMessage()
    {
        var handle = Mock.Of<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        var activity = new Mock<IActivityBroadcaster>();

        var hub = CreateHub(supervisor: supervisor.Object, activity: activity.Object, connectionId: "conn-1");

        await hub.Steer(AgentId.From("agent-a"), SessionId.From("sess-1"), "nudge", "conv-42");

        // Verify SteeringInjected activity was published with the conversation id
        activity.Verify(a => a.PublishAsync(
            It.Is<GatewayActivity>(ga => ga.Type == GatewayActivityType.SteeringInjected && ga.ConversationId == "conv-42"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetSession_WithConversation_DelegatesToResetService_WithExpectedSessionId()
    {
        var caller = new Mock<IGatewayHubClient>();
        caller.Setup(proxy => proxy.SessionReset(It.IsAny<SessionResetPayload>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients<IGatewayHubClient>>();
        clients.SetupGet(value => value.Caller).Returns(caller.Object);

        var conversationId = ConversationId.From("conv-1");
        var sessionId = SessionId.From("session-1");
        var agentId = AgentId.From("agent-a");

        var session = new GatewaySession { SessionId = sessionId, AgentId = agentId };
        session.Session.ConversationId = conversationId;

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(sessionId, CancellationToken.None)).ReturnsAsync(session);

        var resetService = new Mock<IConversationResetService>();
        resetService.Setup(s => s.ResetActiveSessionAsync(conversationId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResetResult(ConversationResetOutcome.Reset, sessionId, agentId));

        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);

        var hub = CreateHub(clients: clients.Object, sessions: sessions.Object, supervisor: supervisor.Object, resetService: resetService.Object);

        await hub.ResetSession(agentId, sessionId);

        resetService.Verify(s => s.ResetActiveSessionAsync(conversationId, sessionId, It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(s => s.ArchiveAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        sessions.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
        supervisor.Verify(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        caller.Verify(c => c.SessionReset(It.Is<SessionResetPayload>(p => p.AgentId == "agent-a" && p.SessionId == "session-1")), Times.Once);
    }

    [Fact]
    public async Task ResetSession_OrphanSession_NoConversation_SealsInPlace_DoesNotArchive()
    {
        var caller = new Mock<IGatewayHubClient>();
        caller.Setup(proxy => proxy.SessionReset(It.IsAny<SessionResetPayload>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients<IGatewayHubClient>>();
        clients.SetupGet(value => value.Caller).Returns(caller.Object);

        var sessionId = SessionId.From("session-orphan");
        var agentId = AgentId.From("agent-a");
        var session = new GatewaySession { SessionId = sessionId, AgentId = agentId };
        // No ConversationId — simulates legacy orphan.

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(sessionId, CancellationToken.None)).ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, CancellationToken.None)).Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.StopAsync(agentId, sessionId, CancellationToken.None)).Returns(Task.CompletedTask);

        var resetService = new Mock<IConversationResetService>(MockBehavior.Strict);

        var hub = CreateHub(clients: clients.Object, sessions: sessions.Object, supervisor: supervisor.Object, resetService: resetService.Object);

        await hub.ResetSession(agentId, sessionId);

        sessions.Verify(s => s.ArchiveAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        sessions.Verify(s => s.SaveAsync(session, CancellationToken.None), Times.Once);
        supervisor.Verify(s => s.StopAsync(agentId, sessionId, CancellationToken.None), Times.Once);
        resetService.Verify(s => s.ResetActiveSessionAsync(It.IsAny<ConversationId>(), It.IsAny<SessionId?>(), It.IsAny<CancellationToken>()), Times.Never);
        session.Session.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
    }

    [Fact]
    public async Task ResetSession_UnknownSession_NotifiesCallerWithoutFailing()
    {
        var caller = new Mock<IGatewayHubClient>();
        caller.Setup(proxy => proxy.SessionReset(It.IsAny<SessionResetPayload>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients<IGatewayHubClient>>();
        clients.SetupGet(value => value.Caller).Returns(caller.Object);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>())).ReturnsAsync((GatewaySession?)null);

        var hub = CreateHub(clients: clients.Object, sessions: sessions.Object);

        await hub.ResetSession(AgentId.From("agent-a"), SessionId.From("missing"));

        caller.Verify(c => c.SessionReset(It.IsAny<SessionResetPayload>()), Times.Once);
        sessions.Verify(s => s.ArchiveAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompactSession_Hub_ReturnsCompactionStats()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetAsync(SessionId.From("session-1"), CancellationToken.None)).ReturnsAsync(session);
        sessions.Setup(value => value.SaveAsync(session, CancellationToken.None)).Returns(Task.CompletedTask);

        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(value => value.CompactAsync(session, It.IsAny<CompactionOptions>(), CancellationToken.None))
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

        var result = await hub.CompactSession(AgentId.From("agent-a"), SessionId.From("session-1"));

        result.Summarized.ShouldBe(5);
        result.Preserved.ShouldBe(3);
        result.TokensBefore.ShouldBe(2000);
        result.TokensAfter.ShouldBe(800);
    }

    [Fact]
    public async Task CompactSession_Hub_SessionNotFound_ThrowsHubException()
    {
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetAsync(SessionId.From("missing"), CancellationToken.None)).ReturnsAsync((GatewaySession?)null);
        var hub = CreateHub(sessions: sessions.Object);

        Func<Task> act = () => hub.CompactSession(AgentId.From("agent-a"), SessionId.From("missing"));

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldBe("Session 'missing' not found.");
    }

    [Fact]
    public async Task InterruptAndSteer_WhenHandleExists_CallsInterruptAndSteerAsyncAndReturnsTrue()
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.InterruptAndSteerAsync("new direction", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(AgentId.From("agent-a"), SessionId.From("session-1")))
            .Returns(handle.Object);

        var hub = CreateHub(supervisor: supervisor.Object);

        var result = await hub.InterruptAndSteer(AgentId.From("agent-a"), SessionId.From("session-1"), "new direction");

        result.ShouldBeTrue();
        handle.Verify(h => h.InterruptAndSteerAsync("new direction", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InterruptAndSteer_WhenNoHandleExists_ReturnsFalseWithoutThrow()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns((IAgentHandle?)null);

        var hub = CreateHub(supervisor: supervisor.Object);

        var result = await hub.InterruptAndSteer(AgentId.From("agent-a"), SessionId.From("missing"), "steer me");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task InterruptAndSteer_NullOrEmptyMessage_ThrowsArgumentException()
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.InterruptAndSteer(AgentId.From("agent-a"), SessionId.From("session-1"), "");

        await act.ShouldThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// Exposed for cross-class test usage (e.g. auth verification tests).
    /// </summary>
    internal static GatewayHub CreateHubForTest(
        IInboundMessageOrchestrator? orchestrator = null,
        ISessionStore? sessions = null,
        string connectionId = "conn-test",
        string? userIdentifier = "user")
        => CreateHub(orchestrator: orchestrator, sessions: sessions, connectionId: connectionId, userIdentifier: userIdentifier);

    private static GatewayHub CreateHub(
        IHubCallerClients<IGatewayHubClient>? clients = null,
        IGroupManager? groups = null,
        ISessionStore? sessions = null,
        IInboundMessageOrchestrator? orchestrator = null,
        IActivityBroadcaster? activity = null,
        IAgentRegistry? registry = null,
        IAgentSupervisor? supervisor = null,
        ISessionCompactor? compactor = null,
        ISessionWarmupService? warmup = null,
        IOptionsMonitor<CompactionOptions>? compactionOptions = null,
        IConversationDispatcher? conversationDispatcher = null,
        IConversationStore? conversationStore = null,
        IAskUserResponseRegistry? askUserResponseRegistry = null,
        IConversationResetService? resetService = null,
        string connectionId = "conn-test",
        string? userIdentifier = "user")
    {
        var sessionStore = sessions ?? new InMemorySessionStore();
        var convStore = conversationStore ?? new InMemoryConversationStore();
        var router = new DefaultConversationRouter(
            convStore,
            sessionStore,
            NullLogger<DefaultConversationRouter>.Instance);
        var dispatcherForHub = conversationDispatcher ?? new DefaultConversationDispatcher(router, convStore);

        var supervisorImpl = supervisor ?? Mock.Of<IAgentSupervisor>();
        var compactorImpl = compactor ?? Mock.Of<ISessionCompactor>();
        var optionsImpl = compactionOptions ?? new TestOptionsMonitor<CompactionOptions>(new CompactionOptions());
        var coordinator = new SessionCompactionCoordinator(
            compactorImpl,
            sessionStore,
            supervisorImpl,
            Mock.Of<IChannelManager>(),
            optionsImpl,
            NullLogger<SessionCompactionCoordinator>.Instance);

        var hub = new GatewayHub(
            supervisorImpl,
            registry ?? Mock.Of<IAgentRegistry>(),
            sessionStore,
            orchestrator ?? new CapturingInboundMessageOrchestrator(),
            activity ?? Mock.Of<IActivityBroadcaster>(),
            compactorImpl,
            warmup ?? Mock.Of<ISessionWarmupService>(),
            dispatcherForHub,
            router,
            optionsImpl,
            NullLogger<GatewayHub>.Instance,
            convStore,
            askUserResponseRegistry,
            resetService,
            coordinator)
        {
            Clients = clients ?? Mock.Of<IHubCallerClients<IGatewayHubClient>>(),
            Groups = groups ?? Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext(connectionId, userIdentifier)
        };

        return hub;
    }

    private sealed class TestHubCallerContext(string connectionId, string? userIdentifier = "user") : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];

        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier { get; } = userIdentifier;
        public override ClaimsPrincipal? User { get; } = new();
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }
    }

    // IsBenignConnectionException classification tests
    [Fact]
    public void IsBenignConnectionException_WithPreHandshakeMessageString_ReturnsTrue()
    {
        var ex = new System.Net.WebSockets.WebSocketException("WebSocket was closed before the connection was established");
        GatewayHub.IsBenignConnectionException(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsBenignConnectionException_WithConnectionClosedPrematurely_ReturnsTrue()
    {
        var ex = new System.Net.WebSockets.WebSocketException(
            System.Net.WebSockets.WebSocketError.ConnectionClosedPrematurely,
            "Connection closed prematurely");
        GatewayHub.IsBenignConnectionException(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsBenignConnectionException_WithOperationCanceledException_ReturnsTrue()
    {
        var ex = new OperationCanceledException("Request was cancelled");
        GatewayHub.IsBenignConnectionException(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsBenignConnectionException_WithIoWrappedWebSocketException_ReturnsTrue()
    {
        var inner = new System.Net.WebSockets.WebSocketException("WebSocket was closed before the connection was established");
        var ex = new System.IO.IOException("An existing connection was forcibly closed", inner);
        GatewayHub.IsBenignConnectionException(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsBenignConnectionException_WithGenericException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Something went wrong");
        GatewayHub.IsBenignConnectionException(ex).ShouldBeFalse();
    }

    [Fact]
    public void IsBenignConnectionException_WithUnrelatedWebSocketException_ReturnsFalse()
    {
        var ex = new System.Net.WebSockets.WebSocketException(
            System.Net.WebSockets.WebSocketError.InvalidMessageType,
            "Unexpected message type");
        GatewayHub.IsBenignConnectionException(ex).ShouldBeFalse();
    }

    // Authentication-related tests (#567)

    [Fact]
    public async Task GatewayHub_SendMessage_UsesAuthenticatedUserIdAsSender()
    {
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1", userIdentifier: "user-oid-abc123");

        await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        // Sender should be the claims-derived user ID, not the connection ID
        dispatched.Sender.Value.ShouldBe("user-oid-abc123");
        dispatched.Sender.Kind.ShouldBe(BotNexus.Domain.World.CitizenKind.User);
        // SenderId should still be the connection ID (for wire-level fan-out exclusion)
        dispatched.SenderId.ShouldBe("conn-1");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_NullUserIdentifier_FallsBackToConnectionId()
    {
        var orchestrator = new CapturingInboundMessageOrchestrator();

        // Simulate edge case where UserIdentifier is null (transition period)
        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-fallback", userIdentifier: null);

        await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        // Falls back to connectionId when no UserIdentifier is available
        dispatched.Sender.Value.ShouldBe("conn-fallback");
        dispatched.SenderId.ShouldBe("conn-fallback");
    }

    [Fact]
    public void GatewayHub_HasAuthorizeAttribute()
    {
        var attributes = typeof(GatewayHub)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .ToArray();
        attributes.ShouldNotBeEmpty("GatewayHub must have [Authorize] to enforce authentication when configured");
        attributes.Single().Policy.ShouldBe(SignalRAuthPolicy.PolicyName);
    }
}
