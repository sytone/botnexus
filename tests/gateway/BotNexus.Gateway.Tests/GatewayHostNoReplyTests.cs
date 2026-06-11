using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Core.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Verifies that NO_REPLY responses from agents do not create empty session entries
/// in the session store (#1237).
/// </summary>
public sealed class GatewayHostNoReplyTests
{
    [Theory]
    [InlineData("NO_REPLY")]
    [InlineData("  NO_REPLY  ")]
    [InlineData("NO_REPLY\n")]
    public async Task DispatchAsync_WhenAgentReturnsNoReply_DoesNotAddAssistantSessionEntry(string noReplyContent)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", noReplyContent);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From("agent-a"),
                SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-1"),
            AgentId = AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                SessionId.From("session-1"),
                AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object, activity,
            CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        // Session should have the user entry but NOT the NO_REPLY assistant entry
        var assistantEntries = session.History.Where(e => e.Role == MessageRole.Assistant).ToList();
        assistantEntries.ShouldBeEmpty();
        // User message should still be recorded
        session.History.ShouldContain(e => e.Role == MessageRole.User && e.Content == "hello");
    }

    [Fact]
    public async Task DispatchAsync_WhenAgentReturnsNormalContent_AddsAssistantSessionEntry()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "Hello, world!");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From("agent-a"),
                SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-1"),
            AgentId = AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                SessionId.From("session-1"),
                AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object, activity,
            CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        // Normal responses should still be recorded
        session.History.ShouldContain(e => e.Role == MessageRole.Assistant && e.Content == "Hello, world!");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Mock<IAgentHandle> CreatePromptHandle(string agentId, string sessionId, string content)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(agentId));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From(sessionId));
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
        channel.SetupGet(c => c.SupportsThinkingDisplay).Returns(false);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
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
        IChannelManager channelManager)
        => new(
            supervisor,
            router,
            sessions,
            activity,
            channelManager,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);

    private static InboundMessage CreateMessage(
        string content,
        string? sessionId = null,
        string conversationId = "conv-1",
        string channelType = "web")
        => new()
        {
            ChannelType = channelType,
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From(conversationId),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null),
            Metadata = new Dictionary<string, object?>()
        };

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];
        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }
        public IAsyncEnumerable<GatewayActivity> SubscribeAsync(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<GatewayActivity>();
    }
}
