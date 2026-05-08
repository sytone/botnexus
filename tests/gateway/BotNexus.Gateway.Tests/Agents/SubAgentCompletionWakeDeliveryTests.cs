using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentCompletionWakeDeliveryTests
{
    [Fact]
    public async Task InternalChannelAdapter_SendStreamEventAsync_DelegatesToTargetAdapter()
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetAsync(SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = SessionId.From("parent-session"),
                AgentId = AgentId.From("parent-agent"),
                ChannelType = ChannelKey.From("signalr")
            });

        var targetAdapter = new Mock<IChannelAdapter>();
        targetAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        targetAdapter.SetupGet(a => a.DisplayName).Returns("SignalR");
        targetAdapter.SetupGet(a => a.SupportsStreaming).Returns(true);
        var targetStreamAdapter = targetAdapter.As<IStreamEventChannelAdapter>();
        targetStreamAdapter
            .Setup(a => a.SendStreamEventAsync("parent-session", It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager
            .Setup(m => m.Get(ChannelKey.From("signalr")))
            .Returns(targetAdapter.Object);

        var sut = CreateInternalAdapter(channelManager.Object, sessionStore.Object);
        var streamAdapter = (object)sut as IStreamEventChannelAdapter;

        streamAdapter.ShouldNotBeNull();

        await streamAdapter!.SendStreamEventAsync(
            "parent-session",
            new AgentStreamEvent { Type = AgentStreamEventType.MessageStart },
            CancellationToken.None);

        targetStreamAdapter.Verify(
            a => a.SendStreamEventAsync(
                "parent-session",
                It.Is<AgentStreamEvent>(e => e.Type == AgentStreamEventType.MessageStart),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task OnCompleted_WhenDispatchPath_StreamEventsReachChannel()
    {
        var manager = CreateManager(parentIsRunning: false, out _, out var dispatcher);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        InboundMessage? dispatchedMessage = null;
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((message, _) => dispatchedMessage = message)
            .Returns(Task.CompletedTask);

        await manager.OnCompletedAsync(spawned.SubAgentId, "complete");

        dispatchedMessage.ShouldNotBeNull();
        dispatchedMessage!.Metadata.TryGetValue("messageType", out var messageType).ShouldBeTrue();
        messageType.ShouldBe("subagent-completion");

        var channelManager = new Mock<IChannelManager>();
        var internalAdapter = CreateInternalAdapter(channelManager.Object, Mock.Of<ISessionStore>());
        channelManager
            .Setup(m => m.Get(ChannelKey.From("internal")))
            .Returns(internalAdapter);

        var resolvedChannel = channelManager.Object.Get(ChannelKey.From("internal"));
        resolvedChannel.ShouldNotBeNull();
        resolvedChannel.ShouldBeAssignableTo<IStreamEventChannelAdapter>();
    }

    [Fact]
    public async Task OnCompleted_RaceCondition_CompletionNotStranded()
    {
        var parentHandle = new Mock<IAgentHandle>();
        parentHandle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        parentHandle.SetupGet(h => h.SessionId).Returns(SessionId.From("parent-session"));
        parentHandle.Setup(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var childHandle = CreateHangingHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.Is<SessionId>(id => id.Value.Contains("::subagent::", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.GetOrCreateAsync(AgentId.From("parent-agent"), SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentHandle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("parent-agent")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });

        var dispatcher = new Mock<IChannelDispatcher>();
        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        await manager.OnCompletedAsync(spawned.SubAgentId, "Done");

        parentHandle.Verify(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        dispatcher.Verify(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static DefaultSubAgentManager CreateManager(
        bool parentIsRunning,
        out Mock<IAgentHandle> parentHandle,
        out Mock<IChannelDispatcher> dispatcher)
    {
        var childHandle = CreateHangingHandle();
        parentHandle = new Mock<IAgentHandle>();
        parentHandle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        parentHandle.SetupGet(h => h.SessionId).Returns(SessionId.From("parent-session"));
        parentHandle.SetupGet(h => h.IsRunning).Returns(parentIsRunning);
        parentHandle.Setup(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.Is<SessionId>(id => id.Value.Contains("::subagent::", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.GetOrCreateAsync(AgentId.From("parent-agent"), SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentHandle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("parent-agent")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });

        dispatcher = new Mock<IChannelDispatcher>();

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);
    }

    private static InternalChannelAdapter CreateInternalAdapter(IChannelManager channelManager, ISessionStore sessionStore)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IChannelManager))).Returns(channelManager);

        return new InternalChannelAdapter(
            serviceProvider.Object,
            sessionStore,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<InternalChannelAdapter>>());
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work"
        };

    private static Mock<IAgentHandle> CreateHangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        return handle;
    }
}
