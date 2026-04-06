using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = "session-1", AgentId = "agent-a" };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync("session-1", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var activity = new RecordingActivityBroadcaster();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content == "agent-response" && m.SessionId == "session-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        session.History.Select(e => $"{e.Role}:{e.Content}").Should().ContainInOrder("user:hello", "assistant:agent-response");
        activity.Activities.Select(a => a.Type).Should().Contain([GatewayActivityType.MessageReceived, GatewayActivityType.AgentProcessing, GatewayActivityType.AgentCompleted]);
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
        handle.Setup(h => h.StreamAsync("hello", It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello " },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "world" }
            ]));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = "session-1", AgentId = "agent-a" };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync("session-1", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: true);
        var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        session.History.Should().Contain(e => e.Role == "assistant" && e.Content == "hello world");
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
        var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        sessions.Verify(s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_BroadcastsActivityEvents()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var activity = new RecordingActivityBroadcaster();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var host = CreateHost(supervisor.Object, router.Object, sessions, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        activity.Activities.Select(a => a.Type).Should().ContainInOrder(
            GatewayActivityType.MessageReceived,
            GatewayActivityType.AgentProcessing,
            GatewayActivityType.AgentCompleted);
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
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        var host = CreateHost(supervisor.Object, router.Object, sessions, activity, CreateChannelManager(channel.Object));

        var dispatches = Enumerable.Range(1, 20)
            .Select(i => host.DispatchAsync(CreateMessage($"m{i}", conversationId: $"conv-{i}")));
        await Task.WhenAll(dispatches);

        var allSessions = await sessions.ListAsync();
        allSessions.Should().HaveCount(20);
        allSessions.Should().OnlyContain(s => s.History.Count == 2);
        activity.Activities.Count(a => a.Type == GatewayActivityType.Error).Should().Be(0);
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
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = "session-1", AgentId = "agent-a" };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync("session-1", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var activity = new RecordingActivityBroadcaster();
        var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        activity.Activities.Should().Contain(a => a.Type == GatewayActivityType.Error && a.Message == "boom");
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
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "web:conv-1:agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync("web:conv-1:agent-a", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = "web:conv-1:agent-a", AgentId = "agent-a" });
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello"));

        sessions.Verify(s => s.GetOrCreateAsync("web:conv-1:agent-a", "agent-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithSuspendedSession_RejectsNewMessages()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        var session = new GatewaySession { SessionId = "session-1", AgentId = "agent-a", Status = SessionStatus.Suspended };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync("session-1", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("suspended", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);
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
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance("agent-a", "session-1"))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = "agent-a",
                SessionId = "session-1",
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync("session-1", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = "session-1", AgentId = "agent-a" });
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("nudge", sessionId: "session-1", metadata: new Dictionary<string, object?> { ["control"] = "steer" }));

        handle.Verify(h => h.SteerAsync("nudge", It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("Steering", StringComparison.OrdinalIgnoreCase)),
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
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync("session-1", "agent-a");

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            sessionQueueCapacity: 1);

        var first = host.DispatchAsync(CreateMessage("one", sessionId: "session-1"));
        var second = host.DispatchAsync(CreateMessage("two", sessionId: "session-1"));
        await Task.Delay(20);
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
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync("session-1", "agent-a");
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            sessionQueueCapacity: 8);

        var first = host.DispatchAsync(CreateMessage("one", sessionId: "session-1"));
        var second = host.DispatchAsync(CreateMessage("two", sessionId: "session-1"));
        await Task.WhenAll(first, second);

        maxInFlight.Should().Be(1);
        handle.Verify(h => h.PromptAsync("one", It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync("two", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithClosedSession_RejectsMessagesWithoutPromptingAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        var session = new GatewaySession { SessionId = "session-1", AgentId = "agent-a", Status = SessionStatus.Closed };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync("session-1", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.GetAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));
        await host.DispatchAsync(CreateMessage("again", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("cannot accept", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private static Mock<IAgentHandle> CreatePromptHandle(string agentId, string sessionId, string content)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns(sessionId);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        manager.Setup(m => m.Get(It.IsAny<string>())).Returns((string channelType) =>
            adapter is not null && string.Equals(channelType, adapter.ChannelType, StringComparison.OrdinalIgnoreCase)
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
        int sessionQueueCapacity = 64)
        => new(supervisor, router, sessions, activity, channelManager, NullLogger<GatewayHost>.Instance, sessionQueueCapacity);

    private static InboundMessage CreateMessage(
        string content,
        string? sessionId = null,
        string conversationId = "conv-1",
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new()
        {
            ChannelType = "web",
            SenderId = "sender-1",
            ConversationId = conversationId,
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
}
