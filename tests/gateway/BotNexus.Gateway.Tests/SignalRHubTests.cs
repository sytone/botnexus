using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Dispatching;
using BotNexus.Gateway.Tests.Diagnostics;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Claims;
using SessionType = BotNexus.Domain.Primitives.SessionType;

namespace BotNexus.Gateway.Tests;

public sealed class SignalRHubTests
{
    /// <summary>
    /// #1838: the lightweight Ping method is the client-side liveness probe used on mobile app
    /// resume. It must complete with a non-negative server tick value so a completed round-trip
    /// proves the transport is alive end-to-end (unlike client-side HubConnectionState, which
    /// stays Connected on an iOS zombie socket).
    /// </summary>
    [Fact]
    public async Task GatewayHub_Ping_ReturnsServerTicks()
    {
        var hub = CreateHubForTest();

        var ticks = await hub.Ping();

        ticks.ShouldBeGreaterThan(0L);
    }

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
    public async Task GatewayHub_GetAgents_ExcludesSubAgentsAndBuiltins()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(value => value.GetAll()).Returns([
            new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot"
            },
            new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth--subagent--coder--abc"),
                DisplayName = "Farnsworth (coder)",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                Kind = AgentKind.SubAgent
            },
            new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("planner"),
                DisplayName = "Planner",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                Metadata = new Dictionary<string, object?> { ["builtin"] = true }
            }
        ]);

        var hub = CreateHub(registry: registry.Object, connectionId: "conn-1");

        var result = await hub.GetAgents();

        Assert.Single(result);
        Assert.Equal("farnsworth", result[0].AgentId.Value);
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
    public async Task GatewayHub_SendMessage_BuildsInboundMessageWithChannelInvariantFields()
    {
        // F-C-1 (#612): the SendMessage -> DispatchMessageAsync path routes through the shared
        // BuildInboundMessage factory. Assert the channel-invariant fields the factory centralizes
        // (signalr channel type, authenticated sender identity, stable per-agent channel address,
        // and the clientKind + messageType metadata) so a refactor cannot silently change them.
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1", userIdentifier: "user-9");

        await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.ChannelType.ShouldBe(ChannelKey.From("signalr"));
        dispatched.SenderId.ShouldBe("conn-1");
        dispatched.Sender.ShouldBe(CitizenId.Of(UserId.From("user-9")));
        dispatched.ChannelAddress.ShouldBe(ChannelAddress.From("agent-a"));
        dispatched.ContentParts.ShouldBeNull();
        dispatched.Metadata["messageType"].ShouldBe("message");
        dispatched.Metadata["clientKind"].ShouldBe("desktop");
    }

    [Fact]
    public async Task GatewayHub_SendMessageWithMedia_BuildsInboundMessageWithChannelInvariantFieldsAndParts()
    {
        // F-C-1 (#612): the SendMessageWithMedia path shares the same BuildInboundMessage factory
        // but supplies per-call ContentParts and a distinct messageType. Assert both the invariant
        // fields and the media-specific parts survive the factory extraction unchanged.
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1", userIdentifier: "user-9");

        var parts = new List<MediaContentPartDto>
        {
            new() { MimeType = "text/plain", Text = "caption" }
        };

        await hub.SendMessageWithMedia(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello", parts);

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.ChannelType.ShouldBe(ChannelKey.From("signalr"));
        dispatched.SenderId.ShouldBe("conn-1");
        dispatched.Sender.ShouldBe(CitizenId.Of(UserId.From("user-9")));
        dispatched.ChannelAddress.ShouldBe(ChannelAddress.From("agent-a"));
        dispatched.Content.ShouldBe("hello");
        dispatched.ContentParts.ShouldNotBeNull();
        dispatched.ContentParts!.ShouldHaveSingleItem();
        dispatched.Metadata["messageType"].ShouldBe("message-with-media");
        dispatched.Metadata["clientKind"].ShouldBe("desktop");
    }

    [Fact]
    public async Task GatewayHub_SendMessageWithMedia_WithConversationId_RoutesToRequestedConversation()
    {
        const string targetSessionId = "session-target";
        const string targetConversationId = "conv-target";
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher.Setup(value => value.DispatchAsync(
                It.Is<InboundMessageContext>(context => context.RequestedConversationId == ConversationId.From(targetConversationId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(
                    ConversationId.From(targetConversationId),
                    SessionId.From(targetSessionId),
                    false,
                    false)));
        var hub = CreateHub(
            orchestrator: orchestrator,
            connectionId: "conn-1",
            conversationDispatcher: conversationDispatcher.Object);
        var parts = new List<MediaContentPartDto>
        {
            new() { MimeType = "image/png", Base64Data = "AQID", FileName = "pasted.png" }
        };

        var result = await hub.SendMessageWithMedia(
            AgentId.From("agent-a"), ChannelKey.From("signalr"), string.Empty, parts, targetConversationId);

        result.SessionId.ShouldBe(targetSessionId);
        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        var routingHints = Assert.IsType<InboundMessageRoutingHints>(dispatched.RoutingHints);
        routingHints.RequestedConversationId.ShouldBe(ConversationId.From(targetConversationId));
        var contentParts = Assert.IsAssignableFrom<IReadOnlyList<MessageContentPart>>(dispatched.ContentParts);
        var binary = contentParts.ShouldHaveSingleItem().ShouldBeOfType<BinaryContentPart>();
        binary.FileName.ShouldBe("pasted.png");
        binary.Data.ShouldBe(new byte[] { 1, 2, 3 });
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

        // Facade grouping the gateway application collaborators the hub delegates to.
        GatewayHubApplicationService NewApp()
            => new(
                orchestrator,
                Mock.Of<ISessionWarmupService>(),
                conversationDispatcher,
                NewCoordinator(sessionStore));

        // Two hubs with different connection IDs but same underlying stores/router
        var hub1 = new GatewayHub(
            Mock.Of<IAgentSupervisor>(),
            Mock.Of<IAgentRegistry>(),
            sessionStore,
            Mock.Of<IActivityBroadcaster>(),
            router,
            NewApp(),
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
            Mock.Of<IActivityBroadcaster>(),
            router,
            NewApp(),
            NullLogger<GatewayHub>.Instance)
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

        // A RUNNING handle is required for a steer to be applied (a steer only makes sense against
        // an in-flight turn). The hub resolves the live handle via GetHandle first.
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(handle.Object);

        var hub = CreateHub(supervisor: supervisor.Object, connectionId: "conn-1");

        var result = await hub.Steer(AgentId.From("agent-a"), SessionId.From(requestedSessionId), "nudge", null);

        result.SessionId.ShouldBe(requestedSessionId);
        // Verify SteerAsync was called on the handle with the content
        handle.Verify(h => h.SteerAsync("nudge", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GatewayHub_Steer_SetsConversationIdOnDispatchedMessage()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(handle.Object);
        var activity = new Mock<IActivityBroadcaster>();

        var hub = CreateHub(supervisor: supervisor.Object, activity: activity.Object, connectionId: "conn-1");

        await hub.Steer(AgentId.From("agent-a"), SessionId.From("sess-1"), "nudge", "conv-42");

        // Verify SteeringInjected activity was published with the conversation id
        activity.Verify(a => a.PublishAsync(
            It.Is<GatewayActivity>(ga => ga.Type == GatewayActivityType.SteeringInjected && ga.ConversationId == "conv-42"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GatewayHub_Steer_WhenAgentNotRunning_DoesNotInjectOrPersist()
    {
        // Dead-letter guard: steering a session whose handle is idle must NOT enqueue the message
        // (an idle handle's PendingMessageQueue is never drained) nor persist it to history.
        // Regression for the production bug where a steer mis-routed to an unrelated idle session
        // was silently swallowed.
        var idleHandle = new Mock<IAgentHandle>();
        idleHandle.SetupGet(h => h.IsRunning).Returns(false);
        var supervisor = new Mock<IAgentSupervisor>();
        // GetHandle returns the idle handle; GetOrCreateAsync (race fallback) returns the same.
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(idleHandle.Object);
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(idleHandle.Object);

        var sessions = new InMemorySessionStore();
        var hub = CreateHub(supervisor: supervisor.Object, sessions: sessions, connectionId: "conn-1");

        await hub.Steer(AgentId.From("agent-a"), SessionId.From("idle-sess"), "nudge", "conv-1");

        // The steer is NOT injected into the idle handle.
        idleHandle.Verify(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // And the message is NOT persisted into the (would-be phantom) session.
        var persisted = await sessions.GetAsync(SessionId.From("idle-sess"), CancellationToken.None);
        (persisted is null || persisted.Session.History.Count == 0).ShouldBeTrue(
            "A steer against an idle agent must not be persisted into session history.");
    }

    [Fact]
    public async Task GatewayHub_Steer_WhenAgentNotRunning_PublishesErrorActivity()
    {
        // The user must get a clear signal instead of the steer silently vanishing.
        var idleHandle = new Mock<IAgentHandle>();
        idleHandle.SetupGet(h => h.IsRunning).Returns(false);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(idleHandle.Object);
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(idleHandle.Object);
        var activity = new Mock<IActivityBroadcaster>();

        var hub = CreateHub(supervisor: supervisor.Object, activity: activity.Object, connectionId: "conn-1");

        await hub.Steer(AgentId.From("agent-a"), SessionId.From("idle-sess"), "nudge", "conv-1");

        activity.Verify(a => a.PublishAsync(
            It.Is<GatewayActivity>(ga => ga.Type == GatewayActivityType.Error && ga.ConversationId == "conv-1"),
            It.IsAny<CancellationToken>()), Times.Once);
        // SteeringInjected must NOT be published.
        activity.Verify(a => a.PublishAsync(
            It.Is<GatewayActivity>(ga => ga.Type == GatewayActivityType.SteeringInjected),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GatewayHub_Steer_SharedDispatchPath_PublishesActivityWithNormalizedIds()
    {
        // Structural guard (#1625): every control method routes its ids through the single
        // ResolveCallContext normalize step and publishes via the shared PublishActivityAsync
        // envelope path. A padded agent id must surface trimmed on the published activity, and
        // the centralized envelope must still carry the conversation id the caller supplied.
        var idleHandle = new Mock<IAgentHandle>();
        idleHandle.SetupGet(h => h.IsRunning).Returns(false);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(idleHandle.Object);
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(idleHandle.Object);
        var activity = new Mock<IActivityBroadcaster>();

        var hub = CreateHub(supervisor: supervisor.Object, activity: activity.Object, connectionId: "conn-1");

        await hub.Steer(AgentId.From("  agent-a  "), SessionId.From("sess-shared"), "nudge", "conv-shared");

        activity.Verify(a => a.PublishAsync(
            It.Is<GatewayActivity>(ga =>
                ga.Type == GatewayActivityType.Error &&
                ga.AgentId == "agent-a" &&
                ga.SessionId == "sess-shared" &&
                ga.ConversationId == "conv-shared"),
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

    // --- Reserved internal-namespace session-key guard (#1493) ---
    // A client must not be able to steer/abort/compact/reset/inspect a gateway-internal
    // sub-agent or cron session by hand-crafting its id. Each client-callable control method
    // rejects a reserved target with a HubException BEFORE touching the supervisor/session store.

    private const string ReservedSubAgentSessionId = "agent-a::subagent::child";
    private const string ReservedCronSessionId = "cron:job-123:20260617:abc";
    private const string ReservedSessionMessage =
        "Session ID targets a reserved internal namespace and cannot be addressed by a client.";

    [Theory]
    [InlineData(ReservedSubAgentSessionId)]
    [InlineData(ReservedCronSessionId)]
    public async Task Steer_ReservedNamespaceSessionId_ThrowsHubException(string reserved)
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.Steer(AgentId.From("agent-a"), SessionId.From(reserved), "nudge", "conv-1");

        (await act.ShouldThrowAsync<HubException>()).Message.ShouldBe(ReservedSessionMessage);
    }

    [Theory]
    [InlineData(ReservedSubAgentSessionId)]
    [InlineData(ReservedCronSessionId)]
    public async Task InterruptAndSteer_ReservedNamespaceSessionId_ThrowsHubException(string reserved)
    {
        // The guard runs after the message null-check but before any handle resolution,
        // so a reserved id throws even with a non-empty message and no supervisor setup.
        var hub = CreateHub();

        Func<Task> act = () => hub.InterruptAndSteer(AgentId.From("agent-a"), SessionId.From(reserved), "steer");

        (await act.ShouldThrowAsync<HubException>()).Message.ShouldBe(ReservedSessionMessage);
    }

    [Theory]
    [InlineData(ReservedSubAgentSessionId)]
    [InlineData(ReservedCronSessionId)]
    public async Task FollowUp_ReservedNamespaceSessionId_ThrowsHubException(string reserved)
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.FollowUp(AgentId.From("agent-a"), SessionId.From(reserved), "more");

        (await act.ShouldThrowAsync<HubException>()).Message.ShouldBe(ReservedSessionMessage);
    }

    [Theory]
    [InlineData(ReservedSubAgentSessionId)]
    [InlineData(ReservedCronSessionId)]
    public async Task Abort_ReservedNamespaceSessionId_ThrowsHubException(string reserved)
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.Abort(AgentId.From("agent-a"), SessionId.From(reserved));

        (await act.ShouldThrowAsync<HubException>()).Message.ShouldBe(ReservedSessionMessage);
    }

    [Theory]
    [InlineData(ReservedSubAgentSessionId)]
    [InlineData(ReservedCronSessionId)]
    public async Task ResetSession_ReservedNamespaceSessionId_ThrowsHubException(string reserved)
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.ResetSession(AgentId.From("agent-a"), SessionId.From(reserved));

        (await act.ShouldThrowAsync<HubException>()).Message.ShouldBe(ReservedSessionMessage);
    }

    [Theory]
    [InlineData(ReservedSubAgentSessionId)]
    [InlineData(ReservedCronSessionId)]
    public async Task CompactSession_ReservedNamespaceSessionId_ThrowsHubException(string reserved)
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.CompactSession(AgentId.From("agent-a"), SessionId.From(reserved));

        (await act.ShouldThrowAsync<HubException>()).Message.ShouldBe(ReservedSessionMessage);
    }

    [Theory]
    [InlineData(ReservedSubAgentSessionId)]
    [InlineData(ReservedCronSessionId)]
    public void GetAgentStatus_ReservedNamespaceSessionId_ThrowsHubException(string reserved)
    {
        var hub = CreateHub();

        Action act = () => hub.GetAgentStatus(AgentId.From("agent-a"), SessionId.From(reserved));

        act.ShouldThrow<HubException>().Message.ShouldBe(ReservedSessionMessage);
    }

    [Fact]
    public async Task Abort_NormalClientSessionId_PassesGuard_AndReturnsWithoutThrow()
    {
        // A generic (non-reserved) session id must NOT be rejected by the guard. With no live
        // instance the method returns early (no-op) -- proving the guard let it through.
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns((AgentInstance?)null);
        var hub = CreateHub(supervisor: supervisor.Object);

        await Should.NotThrowAsync(() => hub.Abort(AgentId.From("agent-a"), SessionId.From("normal-session")));
    }

    /// <summary>
    /// Exposed for cross-class test usage (e.g. auth verification tests).
    /// </summary>

    // ---- Client-kind connection metadata (#1209) ----

    [Fact]
    public async Task GatewayHub_OnConnected_WithMobileClientQuery_StoresClientKind()
    {
        var hub = CreateHubForOnConnected(connectionId: "conn-1", clientQueryValue: "mobile");

        await hub.OnConnectedAsync();

        hub.Context.Items["clientKind"].ShouldBe("mobile");
    }

    [Fact]
    public async Task GatewayHub_OnConnected_NoClientQuery_DefaultsClientKindToDesktop()
    {
        // Back-compat (AC#5): the existing desktop client connects WITHOUT a client hint
        // and must still resolve to a stable, non-"mobile" kind.
        var hub = CreateHubForOnConnected(connectionId: "conn-1", clientQueryValue: null);

        await hub.OnConnectedAsync();

        hub.Context.Items["clientKind"].ShouldBe("desktop");
    }

    [Fact]
    public async Task GatewayHub_OnConnected_NormalizesClientKindToLowercase()
    {
        var hub = CreateHubForOnConnected(connectionId: "conn-1", clientQueryValue: "Mobile");

        await hub.OnConnectedAsync();

        hub.Context.Items["clientKind"].ShouldBe("mobile");
    }

    [Fact]
    public async Task GatewayHub_OnConnected_SanitizesClientKindBeforeLogging()
    {
        // CodeQL cs/log-forging (alerts #76/#77): the connect-time client hint is a raw,
        // user-controlled query value. A malicious client can embed CR/LF to forge fake log
        // lines. The OnConnected log statement must strip control characters before logging
        // so the emitted message stays on a single line.
        var logger = new FakeLogger<GatewayHub>();
        var hub = CreateHubForOnConnected(
            connectionId: "conn-1",
            clientQueryValue: "mobile\r\n[INF] FORGED ADMIN LOGIN",
            logger: logger);

        await hub.OnConnectedAsync();

        var connectRecord = Assert.Single(
            logger.Entries,
            record => record.Message.Contains("Hub OnConnected"));
        connectRecord.Message.ShouldNotContain("\n");
        connectRecord.Message.ShouldNotContain("\r");
        // The forged second-line marker must not survive as a standalone injected line.
        connectRecord.Message.ShouldNotContain("\n[INF] FORGED ADMIN LOGIN");
    }

    [Fact]
    public async Task GatewayHub_OnConnected_SanitizesClientVersionBeforeLogging()
    {
        // clientVersion is read straight from the query string and logged on the same line;
        // it is just as forge-able as clientKind and must be sanitized too.
        var logger = new FakeLogger<GatewayHub>();
        var hub = CreateHubForOnConnected(
            connectionId: "conn-1",
            clientQueryValue: "desktop",
            clientVersionQueryValue: "1.0\r\nFORGED",
            logger: logger);

        await hub.OnConnectedAsync();

        var connectRecord = Assert.Single(
            logger.Entries,
            record => record.Message.Contains("Hub OnConnected"));
        connectRecord.Message.ShouldNotContain("\n");
        connectRecord.Message.ShouldNotContain("\r");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithMobileClientQuery_StampsClientKindIntoMetadata()
    {
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1", clientQueryValue: "mobile");

        await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.Metadata.ContainsKey("clientKind").ShouldBeTrue();
        dispatched.Metadata["clientKind"].ShouldBe("mobile");
        // The existing messageType entry must be preserved alongside clientKind.
        dispatched.Metadata["messageType"].ShouldBe("message");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_NoClientQuery_DefaultsClientKindToDesktopInMetadata()
    {
        // Back-compat (AC#5): a desktop client that sends no hint still carries a stable kind.
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1", clientQueryValue: null);

        await hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.Metadata["clientKind"].ShouldBe("desktop");
    }

    [Fact]
    public async Task GatewayHub_SendMessageWithMedia_StampsClientKindIntoMetadata()
    {
        var orchestrator = new CapturingInboundMessageOrchestrator();

        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1", clientQueryValue: "mobile");

        var parts = new List<MediaContentPartDto>
        {
            new() { MimeType = "text/plain", Text = "hello" }
        };

        await hub.SendMessageWithMedia(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello", parts);

        var dispatched = orchestrator.Captured.ShouldHaveSingleItem();
        dispatched.Metadata["clientKind"].ShouldBe("mobile");
        dispatched.Metadata["messageType"].ShouldBe("message-with-media");
    }

    internal static GatewayHub CreateHubForTest(
        IInboundMessageOrchestrator? orchestrator = null,
        ISessionStore? sessions = null,
        string connectionId = "conn-test",
        string? userIdentifier = "user")
        => CreateHub(orchestrator: orchestrator, sessions: sessions, connectionId: connectionId, userIdentifier: userIdentifier);

    // Builds a hub wired with the minimal Clients/registry/activity mocks that
    // OnConnectedAsync touches, so client-kind connect tests can exercise the real
    // OnConnectedAsync path (which reads the connect-time query) without NREs (#1209).
    private static GatewayHub CreateHubForOnConnected(
        string connectionId,
        string? clientQueryValue,
        string? clientVersionQueryValue = null,
        ILogger<GatewayHub>? logger = null)
    {
        var caller = new Mock<IGatewayHubClient>();
        caller.Setup(proxy => proxy.Connected(It.IsAny<ConnectedPayload>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients<IGatewayHubClient>>();
        clients.SetupGet(value => value.Caller).Returns(caller.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(value => value.GetAll()).Returns([]);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(value => value.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        return CreateHub(
            clients: clients.Object,
            registry: registry.Object,
            activity: activity.Object,
            connectionId: connectionId,
            clientQueryValue: clientQueryValue,
            clientVersionQueryValue: clientVersionQueryValue,
            logger: logger);
    }

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
        string? userIdentifier = "user",
        string? clientQueryValue = null,
        string? clientVersionQueryValue = null,
        ILogger<GatewayHub>? logger = null,
        string[]? userScopes = null)
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

        // The gateway's inbound-dispatch, warmup, conversation-resolution, compaction, and
        // conversation-reset collaborators now live behind IGatewayHubApplicationService. Compose
        // it from the same substitutes so each hub control method stays individually testable.
        var app = new GatewayHubApplicationService(
            orchestrator ?? new CapturingInboundMessageOrchestrator(),
            warmup ?? Mock.Of<ISessionWarmupService>(),
            dispatcherForHub,
            coordinator,
            resetService);

        var hub = new GatewayHub(
            supervisorImpl,
            registry ?? Mock.Of<IAgentRegistry>(),
            sessionStore,
            activity ?? Mock.Of<IActivityBroadcaster>(),
            router,
            app,
            logger ?? NullLogger<GatewayHub>.Instance,
            convStore,
            askUserResponseRegistry)
        {
            Clients = clients ?? Mock.Of<IHubCallerClients<IGatewayHubClient>>(),
            Groups = groups ?? Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext(connectionId, userIdentifier, clientQueryValue, clientVersionQueryValue, userScopes)
        };

        return hub;
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];

        public TestHubCallerContext(string connectionId, string? userIdentifier = "user", string? clientQueryValue = null, string? clientVersionQueryValue = null, string[]? userScopes = null)
        {
            ConnectionId = connectionId;
            UserIdentifier = userIdentifier;

            // When explicit scopes are supplied, present them as an authenticated principal
            // carrying a single space-delimited OAuth "scope" claim so the per-method
            // least-privilege guard (#1524) can read the caller's granted scopes. When null,
            // the principal carries no scope claim -> guard treats it as legacy full-trust.
            if (userScopes is not null)
            {
                var identity = new ClaimsIdentity(authenticationType: "TestAuth");
                identity.AddClaim(new Claim("scope", string.Join(' ', userScopes)));
                User = new ClaimsPrincipal(identity);
            }
            else
            {
                User = new ClaimsPrincipal();
            }

            var features = new FeatureCollection();
            // Mirror the production transport: the hub reads the connect-time query string
            // via Context.GetHttpContext()?.Request.Query, which resolves through the
            // IHttpContextFeature. Stage a DefaultHttpContext carrying the client query param
            // so OnConnectedAsync can read it back (#1209).
            var httpContext = new DefaultHttpContext();
            var query = new Dictionary<string, StringValues>();
            if (clientQueryValue is not null)
            {
                query["client"] = clientQueryValue;
            }
            if (clientVersionQueryValue is not null)
            {
                query["clientVersion"] = clientVersionQueryValue;
            }
            if (query.Count > 0)
            {
                httpContext.Request.Query = new QueryCollection(query);
            }
            features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = httpContext });
            Features = features;
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier { get; }
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; }
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }

        private sealed class HttpContextFeature : IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
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

    // --- Per-method least-privilege scope guard (#1524) ---
    // Mirrors OpenClaw's isApprovalMethod guard: a connection scoped to read-only must not be
    // able to invoke a write-capable control method. Backward compat: a connection carrying no
    // recognised scope claim (legacy full-trust) may invoke any method.

    private const string ReadScope = HubScopeGuard.ReadScopeValue;
    private const string ControlScope = HubScopeGuard.ControlScopeValue;

    [Fact]
    public async Task Steer_ControlScopedConnection_IsAllowed()
    {
        // Happy path: a control-scoped connection passes the guard and reaches the handle.
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(handle.Object);

        var hub = CreateHub(supervisor: supervisor.Object, connectionId: "conn-1", userScopes: [ControlScope]);

        var result = await hub.Steer(AgentId.From("agent-a"), SessionId.From("sess-1"), "nudge", null);

        result.SessionId.ShouldBe("sess-1");
        handle.Verify(h => h.SteerAsync("nudge", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Steer_ReadOnlyScopedConnection_IsRejected()
    {
        // Sad path: a read-only connection must be rejected BEFORE any supervisor interaction.
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var hub = CreateHub(supervisor: supervisor.Object, connectionId: "conn-1", userScopes: [ReadScope]);

        Func<Task> act = () => hub.Steer(AgentId.From("agent-a"), SessionId.From("sess-1"), "nudge", null);

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldContain("not authorized to invoke 'Steer'");
        supervisor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendMessage_ReadOnlyScopedConnection_IsRejected()
    {
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = CreateHub(orchestrator: orchestrator, connectionId: "conn-1", userScopes: [ReadScope]);

        Func<Task> act = () => hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldContain("not authorized to invoke 'SendMessage'");
        // The message never reached the dispatch pipeline.
        orchestrator.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Abort_ReadOnlyScopedConnection_IsRejected()
    {
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var hub = CreateHub(supervisor: supervisor.Object, connectionId: "conn-1", userScopes: [ReadScope]);

        Func<Task> act = () => hub.Abort(AgentId.From("agent-a"), SessionId.From("sess-1"));

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldContain("not authorized to invoke 'Abort'");
        supervisor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompactSession_ReadOnlyScopedConnection_IsRejected()
    {
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        var hub = CreateHub(sessions: sessions.Object, connectionId: "conn-1", userScopes: [ReadScope]);

        Func<Task> act = () => hub.CompactSession(AgentId.From("agent-a"), SessionId.From("sess-1"));

        (await act.ShouldThrowAsync<HubException>())
            .Message.ShouldContain("not authorized to invoke 'CompactSession'");
        // The guard runs before any session-store lookup.
        sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetAgentStatus_ReadOnlyScopedConnection_IsAllowed()
    {
        // Read-only inspection is permitted for a read-scoped connection.
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns((AgentInstance?)null);
        var hub = CreateHub(supervisor: supervisor.Object, connectionId: "conn-1", userScopes: [ReadScope]);

        var result = hub.GetAgentStatus(AgentId.From("agent-a"), SessionId.From("sess-1"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Steer_NoScopeClaims_LegacyFullTrust_IsAllowed()
    {
        // Back-compat: a connection with NO recognised scope claim is treated as full-trust
        // (existing authenticated clients that are not yet scope-tagged keep working).
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns(handle.Object);

        // userScopes: null -> no scope claim on the principal.
        var hub = CreateHub(supervisor: supervisor.Object, connectionId: "conn-1");

        var result = await hub.Steer(AgentId.From("agent-a"), SessionId.From("sess-1"), "nudge", null);

        result.SessionId.ShouldBe("sess-1");
        handle.Verify(h => h.SteerAsync("nudge", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HubScopeGuard_ControlScope_SatisfiesReadRequirement()
    {
        HubScopeGuard.IsSatisfied([ControlScope], HubScope.Read).ShouldBeTrue();
        HubScopeGuard.IsSatisfied([ControlScope], HubScope.Control).ShouldBeTrue();
    }

    [Fact]
    public void HubScopeGuard_ReadScope_DoesNotSatisfyControlRequirement()
    {
        HubScopeGuard.IsSatisfied([ReadScope], HubScope.Read).ShouldBeTrue();
        HubScopeGuard.IsSatisfied([ReadScope], HubScope.Control).ShouldBeFalse();
    }

}
