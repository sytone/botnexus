using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for mid-turn steering injection (issue #228): user messages received while an agent
/// turn is in progress should be injected as steering rather than queued until turn end.
/// </summary>
public sealed class MidTurnSteeringInjectionTests
{
    [Fact]
    public async Task DispatchAsync_WhenAgentIsRunning_InjectsMessageAsSteering()
    {
        // Arrange: agent is running when the message arrives.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(AgentId.From("agent-a"), SessionId.From("session-1")))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a:session-1",
                AgentId = AgentId.From("agent-a"),
                SessionId = SessionId.From("session-1"),
                IsolationStrategy = "in-process",
                Status = AgentInstanceStatus.Running
            });
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-1"),
            AgentId = AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                SessionId.From("session-1"), AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GatewaySession?)null);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var activityBroadcaster = new Mock<IActivityBroadcaster>();
        activityBroadcaster.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            activityBroadcaster.Object, CreateChannelManager(channel.Object));

        // Act
        await host.DispatchAsync(CreateMessage("stop what you are doing", sessionId: "session-1"));

        // Assert: SteerAsync called with message content.
        handle.Verify(
            h => h.SteerAsync("stop what you are doing", It.IsAny<CancellationToken>()),
            Times.Once,
            "message should be injected as steering when agent is running");

        // Assert: PromptAsync NOT called (no new turn started).
        handle.Verify(
            h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a new turn should not be started when agent is already running");
        handle.Verify(
            h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a new turn should not be started when agent is already running");
    }

    [Fact]
    public async Task DispatchAsync_WhenAgentIsNotRunning_StartsNewTurn()
    {
        // Arrange: agent is NOT running.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "response" });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "response" });

        var supervisor = new Mock<IAgentSupervisor>();
        // No running instance.
        supervisor.Setup(s => s.GetInstance(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns((AgentInstance?)null);
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-1"),
            AgentId = AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                SessionId.From("session-1"), AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GatewaySession?)null);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            Mock.Of<IActivityBroadcaster>(), CreateChannelManager(channel.Object));

        // Act
        await host.DispatchAsync(CreateMessage("start a task", sessionId: "session-1"));

        // Assert: SteerAsync NOT called.
        handle.Verify(
            h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "steering should not be used when agent is not running");
    }

    // ---- Helpers ----------------------------------------------------------------

    private static Mock<IChannelAdapter> CreateChannelAdapter(string channelType, bool supportsStreaming)
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(channelType);
        channel.SetupGet(c => c.DisplayName).Returns(channelType);
        channel.SetupGet(c => c.SupportsStreaming).Returns(supportsStreaming);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return channel;
    }

    private static IChannelManager CreateChannelManager(IChannelAdapter? adapter = null)
    {
        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns(adapter is null ? [] : [adapter]);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((ChannelKey channelType) =>
            adapter is not null && channelType.Equals(adapter.ChannelType) ? adapter : null);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>()))
            .Returns((ChannelKey channelType, string? _) =>
                adapter is not null && channelType.Equals(adapter.ChannelType) ? adapter : null);
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
        string channelType = "web")
        => new()
        {
            ChannelType = channelType,
            SenderId = "sender-1",
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = content,
            SessionId = sessionId,
            Metadata = new Dictionary<string, object?>()
        };
}
