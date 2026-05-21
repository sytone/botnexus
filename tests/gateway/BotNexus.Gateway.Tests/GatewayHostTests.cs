using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;
using ThreadId = BotNexus.Domain.Primitives.ThreadId;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayHostTests
{
    [Fact]
    public async Task DispatchAsync_RoutesMessageToResolvedAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var activity = new RecordingActivityBroadcaster();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content == "agent-response" && m.SessionId == "session-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        session.History.Select(e => $"{e.Role}:{e.Content}").ToList().ShouldBe(new[] { "user:hello", "assistant:agent-response" });
        activity.Activities.Select(a => a.Type).ShouldContain(GatewayActivityType.MessageReceived);
        activity.Activities.Select(a => a.Type).ShouldContain(GatewayActivityType.AgentProcessing);
        activity.Activities.Select(a => a.Type).ShouldContain(GatewayActivityType.AgentCompleted);
    }

    [Fact]
    public async Task DispatchAsync_WithStreaming_RecordsAccumulatedHistory()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello " },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "world" }
            ]));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: true);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        session.History.ShouldContain(e => e.Role == MessageRole.Assistant && e.Content == "hello world");
        channel.Verify(c => c.SendStreamDeltaAsync("conv-1", "hello ", It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.SendStreamDeltaAsync("conv-1", "world", It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithNoRoute_ReturnsGracefully()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var supervisor = new Mock<IAgentSupervisor>();
        var sessions = new Mock<ISessionStore>();
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        sessions.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WithPendingAskUserRequest_ConsumesInboundAsToolResponse()
    {
        var conversationId = BotNexus.Domain.Primitives.ConversationId.From("conversation-ask");
        var sessionId = BotNexus.Domain.Primitives.SessionId.From("session-ask");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(sessionId, BotNexus.Domain.Primitives.AgentId.From("agent-a"));
        session.Session.ConversationId = conversationId;
        await sessions.SaveAsync(session);

        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var supervisor = new Mock<IAgentSupervisor>();

        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher.Setup(dispatcher => dispatcher.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(conversationId, sessionId, false, false)));

        var askUserRegistry = new AskUserResponseRegistry();
        var (requestId, pendingTask) = askUserRegistry.Register(conversationId, TimeSpan.FromMinutes(1));
        var interceptor = new PendingAskUserInterceptor(askUserRegistry);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            conversationDispatcher: conversationDispatcher.Object,
            pendingAskUserInterceptor: interceptor);

        await host.DispatchAsync(CreateMessage("Deploy to staging", sessionId: sessionId.Value, conversationId: conversationId.Value));

        var response = await pendingTask;
        response.RequestId.ShouldBe(requestId);
        response.FreeFormText.ShouldBe("Deploy to staging");
        supervisor.Verify(s => s.GetOrCreateAsync(
            It.IsAny<BotNexus.Domain.Primitives.AgentId>(),
            It.IsAny<BotNexus.Domain.Primitives.SessionId>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_BroadcastsActivityEvents()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var activity = new RecordingActivityBroadcaster();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        activity.Activities.Select(a => a.Type).ToList().ShouldBe(new[] {
            GatewayActivityType.MessageReceived,
            GatewayActivityType.AgentProcessing,
            GatewayActivityType.AgentCompleted });
    }

    [Fact]
    public async Task DispatchAsync_WithConcurrentMessages_ProcessesAllSafely()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("dynamic");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string msg, CancellationToken _) => new AgentResponse { Content = $"echo:{msg}" });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentUserMessage msg, CancellationToken _) => new AgentResponse { Content = $"echo:{msg.Content}" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, activity, CreateChannelManager(channel.Object));

        var dispatches = Enumerable.Range(1, 20)
            .Select(i => host.DispatchAsync(CreateMessage($"m{i}", conversationId: $"conv-{i}")));
        await Task.WhenAll(dispatches);

        var allSessions = await sessions.ListAsync();
        allSessions.Count().ShouldBe(20);
        allSessions.ShouldAllBe(s => s.History.Count == 2);
        activity.Activities.Count(a => a.Type == GatewayActivityType.Error).ShouldBe(0);
    }

    [Fact]
    public async Task DispatchAsync_WhenAgentThrows_PublishesErrorActivity()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.Error && a.Message == "boom");
        sessions.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WithNewConversation_CreatesDerivedSession()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "web:conv-1:agent-a", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("web:conv-1:agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("web:conv-1:agent-a"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("web:conv-1:agent-a"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") });
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello"));

        sessions.Verify(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("web:conv-1:agent-a"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_SetsSessionChannelTypeFromInboundMessage()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "cron:job-1:run-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("cron:job-1:run-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("cron:job-1:run-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("cron:job-1:run-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("run", channelType: "cron", conversationId: "cron:job-1:run-1", sessionId: "cron:job-1:run-1"));

        session.ChannelType.ShouldBe(ChannelKey.From("cron"));
    }

    [Fact]
    public async Task DispatchAsync_ExpiredSession_ReactivatesAndProcesses()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync("session-1", "agent-a");
        session.Status = SessionStatus.Expired;
        session.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        await sessions.SaveAsync(session);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        var reloaded = await sessions.GetAsync("session-1");
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(SessionStatus.Active);
        reloaded.ExpiresAt.ShouldBeNull();
        reloaded.History.Select(e => $"{e.Role}:{e.Content}").ToList().ShouldBe(new[] { "user:hello", "assistant:agent-response" });
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content == "agent-response" && m.SessionId == "session-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_SealedSession_ReactivatesAndProcesses()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync("session-1", "agent-a");
        session.Status = SessionStatus.Sealed;
        await sessions.SaveAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        var reloaded = await sessions.GetAsync("session-1");
        reloaded!.Status.ShouldBe(SessionStatus.Active);
        supervisor.Verify(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_SuspendedSession_RejectsMessage()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync("session-1", "agent-a");
        session.Status = SessionStatus.Suspended;
        await sessions.SaveAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("suspended", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_ExpiredSession_ClearsExpiresAt()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync("session-1", "agent-a");
        session.Status = SessionStatus.Expired;
        session.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await sessions.SaveAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        var reloaded = await sessions.GetAsync("session-1");
        reloaded.ShouldNotBeNull();
        reloaded!.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task DispatchAsync_WithSuspendedSession_RejectsNewMessages()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"), Status = SessionStatus.Suspended };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("suspended", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenSystemPromptNotInitialized_StopsExistingHandleBeforePrompt()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor.Setup(s => s.StopAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.StopAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenSystemPromptInitialized_DoesNotStopExistingHandle()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        session.Metadata["systemPromptInitialized"] = true;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WithSteerControl_RoutesToSteerHandler()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.SetupGet(h => h.IsRunning).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1")))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") });
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager());

        await host.DispatchAsync(CreateMessage("nudge", sessionId: "session-1", metadata: new Dictionary<string, object?> { ["control"] = "steer" }));

        handle.Verify(h => h.SteerAsync("nudge", It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WithSteerControl_WhenAgentNotRunning_FallsThroughToNormalProcessing()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "response" });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "response" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1")))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("nudge", sessionId: "session-1", metadata: new Dictionary<string, object?> { ["control"] = "steer" }));

        // Steering was NOT called because agent is not running
        handle.Verify(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // Instead, message was processed normally via PromptAsync
        handle.Verify(h => h.PromptAsync(It.Is<AgentUserMessage>(m => m.Content == "nudge"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithOriginatingBinding_PreservesSourceMetadataOnOutboundMessage()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-target", "reply");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-target"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var sourceBinding = new ChannelBinding
        {
            BindingId = BotNexus.Domain.Primitives.BindingId.From("binding-source"),
            ChannelType = BotNexus.Domain.Primitives.ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("chat-42"),
            ThreadId = ThreadId.From("42"),
            DisplayPrefix = "[topic] ",
            Mode = BindingMode.Interactive
        };
        var conversation = new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.From("conv-target"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelBindings = [sourceBinding]
        };
        var conversationRouter = new Mock<IConversationRouter>();
        conversationRouter.Setup(r => r.GetOutboundBindingsAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-target"),
                sourceBinding.BindingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher.Setup(d => d.DispatchAsync(
                It.IsAny<InboundMessageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                new ChannelSource(
                    sourceBinding.ChannelType,
                    sourceBinding.ChannelAddress,
                    context.Source.SenderId,
                    sourceBinding.ThreadId,
                    sourceBinding.BindingId,
                    sourceBinding.DisplayPrefix),
                new ConversationSessionResolution(
                    conversation.ConversationId,
                    BotNexus.Domain.Primitives.SessionId.From("session-target"),
                    false,
                    false,
                    sourceBinding.BindingId,
                    sourceBinding.ThreadId,
                    sourceBinding.DisplayPrefix)));

        var channel = CreateChannelAdapter("telegram", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            conversationDispatcher: conversationDispatcher.Object,
            conversationRouter: conversationRouter.Object);

        await host.DispatchAsync(CreateMessage("hello", channelType: "telegram", conversationId: "chat-42"));

        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m =>
                    m.ChannelType == BotNexus.Domain.Primitives.ChannelKey.From("telegram") &&
                    m.SessionId == "session-target" &&
                    m.ThreadId == ThreadId.From("42") &&
                    m.BindingId == sourceBinding.BindingId &&
                    m.DisplayPrefix == "[topic] "),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_InternalWakeMessage_WithSessionId_BypassesConversationDispatcher()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "parent-session", "wake-ack");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var parentSession = await sessions.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            BotNexus.Domain.Primitives.AgentId.From("agent-a"));
        parentSession.Session.ConversationId = BotNexus.Domain.Primitives.ConversationId.From("conv-parent");
        await sessions.SaveAsync(parentSession);

        var conversationDispatcher = new Mock<IConversationDispatcher>();

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            conversationDispatcher: conversationDispatcher.Object);

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = BotNexus.Domain.Primitives.ChannelKey.From("internal"),
            SenderId = "subagent:test",
            ChannelAddress = ChannelAddress.From("parent-session"),
            SessionId = "parent-session",
            TargetAgentId = "agent-a",
            Content = "subagent completion wake-up",
            Metadata = new Dictionary<string, object?> { ["messageType"] = "subagent-completion" }
        });

        conversationDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        supervisor.Verify(
            s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var reloaded = await sessions.GetAsync(BotNexus.Domain.Primitives.SessionId.From("parent-session"));
        reloaded.ShouldNotBeNull();
        reloaded!.History.Count.ShouldBe(2);
        reloaded.Session.ConversationId.ShouldBe(BotNexus.Domain.Primitives.ConversationId.From("conv-parent"));
    }

    [Fact]
    public async Task DispatchAsync_InternalWakeMessage_WithoutSessionId_UsesChannelAddressAndBypassesConversationDispatcher()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "parent-session", "wake-ack");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var parentSession = await sessions.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            BotNexus.Domain.Primitives.AgentId.From("agent-a"));
        parentSession.Session.ConversationId = BotNexus.Domain.Primitives.ConversationId.From("conv-parent");
        await sessions.SaveAsync(parentSession);

        var conversationDispatcher = new Mock<IConversationDispatcher>();

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            conversationDispatcher: conversationDispatcher.Object);

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = BotNexus.Domain.Primitives.ChannelKey.From("internal"),
            SenderId = "subagent:test",
            ChannelAddress = ChannelAddress.From("parent-session"),
            TargetAgentId = "agent-a",
            Content = "subagent completion wake-up",
            Metadata = new Dictionary<string, object?> { ["messageType"] = "subagent-completion" }
        });

        conversationDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        supervisor.Verify(
            s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHost_AutoCompaction_TriggersWhenAboveThreshold()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(c => c.ShouldCompact(session.Session, It.IsAny<CompactionOptions>())).Returns(true);
        compactor.Setup(c => c.CompactAsync(session.Session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompactionResult { Summary = string.Empty });

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            compactor: compactor.Object);

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        compactor.Verify(c => c.CompactAsync(session.Session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GatewayHost_AutoCompaction_DoesNotTriggerBelowThreshold()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(c => c.ShouldCompact(session.Session, It.IsAny<CompactionOptions>())).Returns(false);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            compactor: compactor.Object);

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        compactor.Verify(c => c.CompactAsync(session.Session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GatewayHost_AutoCompaction_FailureDoesNotBlockProcessing()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(c => c.ShouldCompact(session.Session, It.IsAny<CompactionOptions>())).Returns(true);
        compactor.Setup(c => c.CompactAsync(session.Session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("compaction failed"));
        var channel = CreateChannelAdapter("web", supportsStreaming: false);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            compactor: compactor.Object);

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content == "agent-response"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenSessionQueueIsFull_ReturnsBusyResponse()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(250);
                return new AgentResponse { Content = "ok" };
            });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(250);
                return new AgentResponse { Content = "ok" };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync("session-1", "agent-a");

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            sessionQueueCapacity: 1);

        var first = host.DispatchAsync(CreateMessage("one", sessionId: "session-1"));
        var second = host.DispatchAsync(CreateMessage("two", sessionId: "session-1"));
        // Yield until the first dispatch is actively running (PromptAsync takes 250ms, so both
        // are in-flight by the time we need to verify busy rejection).
        await Task.WhenAny(first, second, Task.Delay(100));
        await host.DispatchAsync(CreateMessage("three", sessionId: "session-1"));
        await Task.WhenAll(first, second);

        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("busy", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DispatchAsync_WithSameSessionMessages_ProcessesQueueSequentially()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var inFlight = 0;
        var maxInFlight = 0;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (message, _) =>
            {
                var current = Interlocked.Increment(ref inFlight);
                Interlocked.Exchange(ref maxInFlight, Math.Max(maxInFlight, current));
                await Task.Delay(75);
                Interlocked.Decrement(ref inFlight);
                return new AgentResponse { Content = $"echo:{message}" };
            });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns<AgentUserMessage, CancellationToken>(async (message, _) =>
            {
                var current = Interlocked.Increment(ref inFlight);
                Interlocked.Exchange(ref maxInFlight, Math.Max(maxInFlight, current));
                await Task.Delay(75);
                Interlocked.Decrement(ref inFlight);
                return new AgentResponse { Content = $"echo:{message.Content}" };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync("session-1", "agent-a");
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            sessionQueueCapacity: 8);

        var first = host.DispatchAsync(CreateMessage("one", sessionId: "session-1"));
        var second = host.DispatchAsync(CreateMessage("two", sessionId: "session-1"));
        await Task.WhenAll(first, second);

        maxInFlight.ShouldBe(1);
        handle.Verify(h => h.PromptAsync(It.Is<AgentUserMessage>(m => m.Content == "one"), It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync(It.Is<AgentUserMessage>(m => m.Content == "two"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithSuspendedSession_RejectsMessagesWithoutPromptingAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync("session-1", "agent-a");
        session.Status = SessionStatus.Suspended;
        await sessions.SaveAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));
        await host.DispatchAsync(CreateMessage("again", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("suspended", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_WhenStarted_ManagesChannelLifecycleAndShutdown()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.StopAllAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var channelStartedTcs = new TaskCompletionSource();
        var firstChannel = CreateChannelAdapter("web", supportsStreaming: false);
        firstChannel.Setup(c => c.StartAsync(It.IsAny<IChannelDispatcher>(), It.IsAny<CancellationToken>()))
            .Callback(() => channelStartedTcs.TrySetResult())
            .Returns(Task.CompletedTask);
        firstChannel.Setup(c => c.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var secondChannel = CreateChannelAdapter("telegram", supportsStreaming: false);
        secondChannel.Setup(c => c.StartAsync(It.IsAny<IChannelDispatcher>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("start failed"));
        secondChannel.Setup(c => c.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns([firstChannel.Object, secondChannel.Object]);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((ChannelKey channelType) =>
            channelType.Equals(ChannelKey.From("web"))
                ? firstChannel.Object
                : channelType.Equals(ChannelKey.From("telegram"))
                    ? secondChannel.Object
                    : null);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((ChannelKey channelType, string? _) =>
            channelType.Equals(ChannelKey.From("web"))
                ? firstChannel.Object
                : channelType.Equals(ChannelKey.From("telegram"))
                    ? secondChannel.Object
                    : null);

        await using var host = new GatewayHost(
            supervisor.Object,
            Mock.Of<IMessageRouter>(),
            new InMemorySessionStore(),
            new RecordingActivityBroadcaster(),
            manager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        // Wait until the channel adapter's StartAsync has been invoked (event-driven, not time-based).
        await channelStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync(CancellationToken.None);

        firstChannel.Verify(c => c.StartAsync(It.IsAny<IChannelDispatcher>(), It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.StartAsync(It.IsAny<IChannelDispatcher>(), It.IsAny<CancellationToken>()), Times.Once);
        firstChannel.Verify(c => c.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        supervisor.Verify(s => s.StopAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithBinaryImageContentPart_ForwardsImageToAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var imageData = new byte[] { 0xFF, 0xD8, 0xFF }; // minimal JPEG header
        var message = CreateMessage("look at this", sessionId: "session-1") with
        {
            ContentParts = [new BinaryContentPart { MimeType = "image/jpeg", Data = imageData }]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldNotBeNull();
        capturedMessage.Images.ShouldHaveSingleItem();
        capturedMessage.Images![0].Value.ShouldBe($"data:image/jpeg;base64,{Convert.ToBase64String(imageData)}");
    }

    [Fact]
    public async Task DispatchAsync_WithReferenceImageContentPart_ForwardsUrlToAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        const string imageUrl = "https://example.com/photo.jpg";
        var message = CreateMessage("describe this", sessionId: "session-1") with
        {
            ContentParts = [new ReferenceContentPart { MimeType = "image/jpeg", Uri = imageUrl }]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldNotBeNull();
        capturedMessage.Images.ShouldHaveSingleItem();
        capturedMessage.Images![0].Value.ShouldBe(imageUrl);
    }

    [Fact]
    public async Task DispatchAsync_WithNoImageContentParts_UsesStringOverload()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-1", "response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("plain text", sessionId: "session-1"));

        // GatewayHost always calls the UserMessage overload; string overload is never used directly
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithNonImageBinaryPart_ProducesNoImagesInUserMessage()
    {
        // A BinaryContentPart with audio/wav must NOT be converted to an image
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var message = CreateMessage("transcribe this", sessionId: "session-1") with
        {
            ContentParts = [new BinaryContentPart { MimeType = "audio/wav", Data = [0x52, 0x49, 0x46, 0x46] }]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldBeNull();
    }

    [Fact]
    public async Task DispatchAsync_WithMixedImageAndNonImageParts_ForwardsOnlyImageParts()
    {
        // Only image/* MIME types should be forwarded; non-image parts are silently excluded
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var imageData = new byte[] { 0xFF, 0xD8, 0xFF };
        var message = CreateMessage("analyze this", sessionId: "session-1") with
        {
            ContentParts =
            [
                new BinaryContentPart { MimeType = "image/png", Data = imageData },
                new BinaryContentPart { MimeType = "audio/wav", Data = [0x52, 0x49, 0x46, 0x46] },
                new ReferenceContentPart { MimeType = "application/pdf", Uri = "https://example.com/doc.pdf" }
            ]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldNotBeNull();
        capturedMessage.Images!.Count.ShouldBe(1);
        capturedMessage.Images[0].Value.ShouldBe($"data:image/png;base64,{Convert.ToBase64String(imageData)}");
    }

    [Fact]
    public async Task DispatchAsync_WithMultipleImageParts_ForwardsAllImages()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var jpeg1 = new byte[] { 0xFF, 0xD8, 0xFF };
        var png1 = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        const string imageUrl = "https://cdn.example.com/img3.webp";

        var message = CreateMessage("compare these", sessionId: "session-1") with
        {
            ContentParts =
            [
                new BinaryContentPart { MimeType = "image/jpeg", Data = jpeg1 },
                new BinaryContentPart { MimeType = "image/png", Data = png1 },
                new ReferenceContentPart { MimeType = "image/webp", Uri = imageUrl }
            ]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldNotBeNull();
        capturedMessage.Images!.Count.ShouldBe(3);
        capturedMessage.Images[0].Value.ShouldBe($"data:image/jpeg;base64,{Convert.ToBase64String(jpeg1)}");
        capturedMessage.Images[1].Value.ShouldBe($"data:image/png;base64,{Convert.ToBase64String(png1)}");
        capturedMessage.Images[2].Value.ShouldBe(imageUrl);
    }

    [Fact]
    public async Task DispatchAsync_WithEmptyContentParts_ProducesNoImages()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var message = CreateMessage("plain text", sessionId: "session-1") with
        {
            ContentParts = []
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldBeNull();
    }

    private static Mock<IAgentHandle> CreatePromptHandle(string agentId, string sessionId, string content)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns(sessionId);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = content });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = content });
        return handle;
    }

    private static Mock<IChannelAdapter> CreateChannelAdapter(string channelType, bool supportsStreaming)
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(channelType);
        channel.SetupGet(c => c.DisplayName).Returns(channelType);
        channel.SetupGet(c => c.SupportsStreaming).Returns(supportsStreaming);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        channel.Setup(c => c.SendStreamDeltaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return channel;
    }

    private static IChannelManager CreateChannelManager(IChannelAdapter? adapter = null)
    {
        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns(adapter is null ? [] : [adapter]);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((ChannelKey channelType) =>
            adapter is not null && channelType.Equals(adapter.ChannelType)
                ? adapter
                : null);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((ChannelKey channelType, string? _) =>
            adapter is not null && channelType.Equals(adapter.ChannelType)
                ? adapter
                : null);
        return manager.Object;
    }

    private static GatewayHost CreateHost(
        IAgentSupervisor supervisor,
        IMessageRouter router,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IChannelManager channelManager,
        int sessionQueueCapacity = 64,
        ISessionCompactor? compactor = null,
        IOptionsMonitor<CompactionOptions>? compactionOptions = null,
        IConversationDispatcher? conversationDispatcher = null,
        IConversationRouter? conversationRouter = null,
        PendingAskUserInterceptor? pendingAskUserInterceptor = null)
        => new(
            supervisor,
            router,
            sessions,
            activity,
            channelManager,
            compactor ?? Mock.Of<ISessionCompactor>(),
            compactionOptions ?? new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            sessionQueueCapacity,
            conversationDispatcher: conversationDispatcher,
            conversationRouter: conversationRouter,
            pendingAskUserInterceptor: pendingAskUserInterceptor);

    private static InboundMessage CreateMessage(
        string content,
        string? sessionId = null,
        string conversationId = "conv-1",
        string channelType = "web",
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new()
        {
            ChannelType = channelType,
            SenderId = "sender-1",
            ChannelAddress = ChannelAddress.From(conversationId),
            Content = content,
            SessionId = sessionId,
            Metadata = metadata ?? new Dictionary<string, object?>()
        };

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);
            yield break;
        }
    }

    [Fact]
    public async Task ProcessInboundMessage_StampsSessionWithConversationId()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "web:addr-1:agent-a", "response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var expectedConvId = BotNexus.Domain.Primitives.ConversationId.From("c_stamptest1");
        var expectedSessionId = BotNexus.Domain.Primitives.SessionId.From("web:addr-1:agent-a");
        await sessions.GetOrCreateAsync(expectedSessionId, BotNexus.Domain.Primitives.AgentId.From("agent-a"));

        var conversation = new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = expectedConvId,
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
        };
        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher
            .Setup(d => d.DispatchAsync(
                It.IsAny<InboundMessageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(
                    conversation.ConversationId,
                    expectedSessionId,
                    false,
                    false)));

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = new GatewayHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationDispatcher: conversationDispatcher.Object);

        await host.DispatchAsync(CreateMessage("hello", channelType: "web", conversationId: "addr-1"));
        conversationDispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<InboundMessageContext>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce, "IConversationDispatcher.DispatchAsync must be called");

        var savedSession = await sessions.GetAsync(expectedSessionId, CancellationToken.None);
        savedSession.ShouldNotBeNull();
        savedSession!.Session.ConversationId.ShouldNotBeNull("Session.ConversationId must be stamped after inbound message");
        savedSession.Session.ConversationId!.Value.Value.ShouldBe("c_stamptest1");
    }
}

