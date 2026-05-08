using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentArchetypeIdentityTests
{
    [Fact]
    public async Task SpawnAsync_AssignsDistinctArchetypedChildAgentId_WithParentReference()
    {
        var parentAgentId = AgentId.From("parent-agent");
        var parentSessionId = SessionId.From("parent-session");
        AgentId? capturedChildAgentId = null;
        SessionId? capturedChildSessionId = null;

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(parentAgentId)).Returns(new AgentDescriptor
        {
            AgentId = parentAgentId,
            DisplayName = "Parent",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot"
        });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(parentAgentId);
        handle.SetupGet(h => h.SessionId).Returns(parentSessionId);
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new AgentResponse { Content = "never" };
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId, SessionId, CancellationToken>((agentId, sessionId, _) =>
            {
                capturedChildAgentId = agentId;
                capturedChildSessionId = sessionId;
            })
            .ReturnsAsync(handle.Object);

        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<IChannelDispatcher>(),
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);

        var info = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = parentAgentId,
            ParentSessionId = parentSessionId,
            Task = "Investigate issue",
            Archetype = SubAgentArchetype.Reviewer
        });

        capturedChildAgentId.ShouldNotBeNull();
        capturedChildSessionId.ShouldNotBeNull();
        capturedChildAgentId!.Value.Value.ShouldNotBe(parentAgentId.Value);
        capturedChildAgentId.Value.Value.ShouldStartWith($"{parentAgentId.Value}--subagent--reviewer--");
        capturedChildAgentId.Value.Value.ShouldContain($"{parentAgentId.Value}--subagent--");
        capturedChildSessionId!.Value.Value.ShouldContain("::subagent::");
        info.Archetype.ShouldBe(SubAgentArchetype.Reviewer);
    }

    [Fact]
    public async Task SpawnAsync_UsesGeneralArchetypeByDefault()
    {
        var parentAgentId = AgentId.From("parent-agent");
        var parentSessionId = SessionId.From("parent-session");
        AgentId? capturedChildAgentId = null;

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(parentAgentId)).Returns(new AgentDescriptor
        {
            AgentId = parentAgentId,
            DisplayName = "Parent",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot"
        });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(parentAgentId);
        handle.SetupGet(h => h.SessionId).Returns(parentSessionId);
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new AgentResponse { Content = "never" };
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId, SessionId, CancellationToken>((agentId, _, _) => capturedChildAgentId = agentId)
            .ReturnsAsync(handle.Object);

        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<IChannelDispatcher>(),
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);

        var info = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = parentAgentId,
            ParentSessionId = parentSessionId,
            Task = "Investigate issue"
        });

        info.Archetype.ShouldBe(SubAgentArchetype.General);
        capturedChildAgentId.ShouldNotBeNull();
        capturedChildAgentId!.Value.Value.ShouldContain("--subagent--");
        info.ChildSessionId.Value.ShouldContain("::subagent::");
    }
}
