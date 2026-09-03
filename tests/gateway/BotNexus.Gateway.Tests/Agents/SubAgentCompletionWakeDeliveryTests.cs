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
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using BotNexus.Gateway.Tests;

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
            .Setup(a => a.CanSendStreamEvent(It.IsAny<ChannelStreamTarget>()))
            .Returns(true);
        targetStreamAdapter
            .Setup(a => a.SendStreamEventAsync(StreamTargets.For("parent-session"), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager
            .Setup(m => m.Get(ChannelKey.From("signalr")))
            .Returns(targetAdapter.Object);

        var sut = CreateInternalAdapter(channelManager.Object, sessionStore.Object);
        var streamAdapter = (object)sut as IStreamEventChannelAdapter;

        streamAdapter.ShouldNotBeNull();

        await streamAdapter!.SendStreamEventAsync(StreamTargets.For("parent-session"),
            new AgentStreamEvent { Type = AgentStreamEventType.MessageStart },
            CancellationToken.None);

        targetStreamAdapter.Verify(
            a => a.SendStreamEventAsync(StreamTargets.For("parent-session"),
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
        parentHandle.Setup(h => h.FollowUpAsync(It.IsAny<AgentTranscriptMessage>(), It.IsAny<CancellationToken>()))
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

        parentHandle.Verify(h => h.FollowUpAsync(It.IsAny<AgentTranscriptMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        dispatcher.Verify(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// #3703 AC1/AC3/AC4/AC5: when the completion dispatch throws, the record must stop reading as a
    /// clean completion, the lifecycle activity must not be <c>SubAgentCompleted</c>, and the child
    /// must still be torn down.
    /// </summary>
    [Fact]
    public async Task OnCompleted_WhenDispatchThrows_RecordIsDeliveryFailedAndChildStillTornDown()
    {
        var manager = CreateManager(
            parentIsRunning: false,
            out _,
            out var dispatcher,
            out var supervisor,
            out var activities);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("parent session is gone"));

        await manager.OnCompletedAsync(spawned.SubAgentId, "work is done");

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();

        // AC1: observable state distinguishable from a delivered completion.
        info!.CompletionDelivery.ShouldBe(SubAgentCompletionDelivery.Failed);
        info.CompletionDeliveryError.ShouldBe("parent session is gone");
        // The run's own summary survives on the record - it is the only surviving copy.
        info.ResultSummary.ShouldBe("work is done");

        // AC3: the lifecycle activity is NOT SubAgentCompleted.
        var terminal = activities
            .Where(a => a.Type is GatewayActivityType.SubAgentCompleted or GatewayActivityType.SubAgentFailed)
            .ToArray();
        terminal.ShouldNotBeEmpty();
        terminal.ShouldAllBe(a => a.Type != GatewayActivityType.SubAgentCompleted);
        terminal.ShouldContain(a => a.Type == GatewayActivityType.SubAgentFailed);

        // AC4: the teardown `finally` still ran despite the dispatch throwing.
        supervisor.Verify(
            s => s.StopAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.IsAny<SessionId>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// #3703 AC1 (control): a delivery that succeeds must record <c>Delivered</c> and publish the
    /// clean completion activity, so the failure case above is a genuine distinction rather than
    /// both paths reporting the same thing.
    /// </summary>
    [Fact]
    public async Task OnCompleted_WhenDispatchSucceeds_RecordIsDeliveredAndActivityIsCompleted()
    {
        var manager = CreateManager(
            parentIsRunning: false,
            out _,
            out var dispatcher,
            out _,
            out var activities);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await manager.OnCompletedAsync(spawned.SubAgentId, "work is done");

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        info!.CompletionDelivery.ShouldBe(SubAgentCompletionDelivery.Delivered);
        info.CompletionDeliveryError.ShouldBeNull();

        activities.ShouldContain(a => a.Type == GatewayActivityType.SubAgentCompleted);
        activities.ShouldNotContain(a => a.Type == GatewayActivityType.SubAgentFailed);
    }

    /// <summary>
    /// #3703 AC2: both surfaced tools must say delivery failed rather than reporting a clean
    /// completion.
    /// </summary>
    [Fact]
    public async Task DeliveryFailedRecord_IsReportedByListAndStatusTools()
    {
        var manager = CreateManager(parentIsRunning: false, out _, out var dispatcher, out _, out _);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("parent session is gone"));

        await manager.OnCompletedAsync(spawned.SubAgentId, "work is done");

        var parentSession = SessionId.From("parent-session");

        var listResult = await new SubAgentListTool(manager, parentSession)
            .ExecuteAsync("call-1", new Dictionary<string, object?>());
        var listText = listResult.Content[0].Value;
        listText.ShouldNotBeNull();
        listText!.ShouldContain("\"completionDelivery\":\"Failed\"");
        listText.ShouldContain("never reached this session");

        var statusResult = await new SubAgentManageTool(manager, parentSession)
            .ExecuteAsync("call-2", new Dictionary<string, object?>
            {
                ["subAgentId"] = spawned.SubAgentId,
                ["action"] = "status"
            });
        var statusText = statusResult.Content[0].Value;
        statusText.ShouldNotBeNull();
        statusText!.ShouldContain("\"completionDelivery\":\"Failed\"");
        statusText.ShouldContain("parent session is gone");
        statusText.ShouldContain("never reached this session");
    }

    private static DefaultSubAgentManager CreateManager(
        bool parentIsRunning,
        out Mock<IAgentHandle> parentHandle,
        out Mock<IChannelDispatcher> dispatcher)
        => CreateManager(parentIsRunning, out parentHandle, out dispatcher, out _, out _);

    private static DefaultSubAgentManager CreateManager(
        bool parentIsRunning,
        out Mock<IAgentHandle> parentHandle,
        out Mock<IChannelDispatcher> dispatcher,
        out Mock<IAgentSupervisor> supervisor,
        out List<GatewayActivity> activities)
    {
        var childHandle = CreateHangingHandle();
        parentHandle = new Mock<IAgentHandle>();
        parentHandle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        parentHandle.SetupGet(h => h.SessionId).Returns(SessionId.From("parent-session"));
        parentHandle.SetupGet(h => h.IsRunning).Returns(parentIsRunning);
        parentHandle.Setup(h => h.FollowUpAsync(It.IsAny<AgentTranscriptMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        supervisor = new Mock<IAgentSupervisor>();
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

        var captured = new List<GatewayActivity>();
        activities = captured;
        var broadcaster = new Mock<IActivityBroadcaster>();
        broadcaster
            .Setup(b => b.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Callback<GatewayActivity, CancellationToken>((activity, _) =>
            {
                lock (captured)
                {
                    captured.Add(activity);
                }
            })
            .Returns(ValueTask.CompletedTask);

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            broadcaster.Object,
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
            Task = "Do background work",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
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
