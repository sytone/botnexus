using BotNexus.Domain.Primitives;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentCompletionWakeUpTests
{
    [Fact]
    public async Task OnCompleted_WhenParentIdle_DispatchesWakeUpMessage()
    {
        var manager = CreateManager(parentIsRunning: false, out var parentHandle, out _, out var dispatcher);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        var summary = "Completed investigation and found root cause.";

        await manager.OnCompletedAsync(spawned.SubAgentId, summary);

        parentHandle.Verify(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        dispatcher.Verify(d => d.DispatchAsync(
                It.Is<InboundMessage>(message =>
                    message.ChannelType.Value == "internal" &&
                    message.SenderId.StartsWith("subagent:", StringComparison.Ordinal) &&
                    message.Content.Contains(summary, StringComparison.Ordinal) &&
                    message.Metadata.ContainsKey("messageType") &&
                    string.Equals(message.Metadata["messageType"] as string, "subagent-completion", StringComparison.Ordinal) &&
                    message.SessionId == "parent-session" &&
                    message.TargetAgentId == "parent-agent"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCompleted_WhenParentRunning_EnqueuesFollowUp()
    {
        var manager = CreateManager(parentIsRunning: true, out var parentHandle, out _, out var dispatcher);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        SubAgentCompletionMessage? capturedMessage = null;
        parentHandle
            .Setup(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentMessage, CancellationToken>((message, _) => capturedMessage = message as SubAgentCompletionMessage)
            .Returns(Task.CompletedTask);

        await manager.OnCompletedAsync(spawned.SubAgentId, "Done");

        parentHandle.Verify(h => h.FollowUpAsync(
                It.IsAny<AgentMessage>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        capturedMessage.Should().NotBeNull();
        capturedMessage!.SubAgentId.Should().Be(spawned.SubAgentId);
        dispatcher.Verify(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnCompleted_WhenDispatchFails_LogsWarningAndContinues()
    {
        var manager = CreateManager(parentIsRunning: false, out _, out _, out var dispatcher);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dispatch failed"));

        var act = () => manager.OnCompletedAsync(spawned.SubAgentId, "Done");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnCompleted_WakeUpMessage_ContainsCorrectSubAgentId()
    {
        var manager = CreateManager(parentIsRunning: false, out _, out _, out var dispatcher);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await manager.OnCompletedAsync(spawned.SubAgentId, "Done");

        dispatcher.Verify(d => d.DispatchAsync(
                It.Is<InboundMessage>(message =>
                    message.Metadata.ContainsKey("subAgentId") &&
                    string.Equals(message.Metadata["subAgentId"] as string, spawned.SubAgentId, StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCompleted_WakeUpMessage_UsesInternalChannelType()
    {
        var manager = CreateManager(parentIsRunning: false, out _, out _, out var dispatcher);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await manager.OnCompletedAsync(spawned.SubAgentId, "Done");

        dispatcher.Verify(d => d.DispatchAsync(
                It.Is<InboundMessage>(message => message.ChannelType.Value == "internal"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static DefaultSubAgentManager CreateManager(
        bool parentIsRunning,
        out Mock<IAgentHandle> parentHandle,
        out Mock<IAgentSupervisor> supervisor,
        out Mock<IChannelDispatcher> dispatcher)
    {
        var childHandle = CreateHangingHandle();
        parentHandle = CreateParentHandle(parentIsRunning);

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

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work"
        };

    private static Mock<IAgentHandle> CreateParentHandle(bool isRunning)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("parent-session"));
        handle.SetupGet(h => h.IsRunning).Returns(isRunning);
        handle.Setup(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

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
