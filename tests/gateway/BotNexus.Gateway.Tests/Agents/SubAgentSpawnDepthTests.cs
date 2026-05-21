using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Verifies that <see cref="DefaultSubAgentManager"/> enforces the configured maximum
/// spawn depth, preventing unbounded sub-agent trees.
/// </summary>
public sealed class SubAgentSpawnDepthTests
{
    [Fact]
    public async Task SpawnAsync_AtMaxDepth_ThrowsInvalidOperation()
    {
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        // MaxDepth = 1 means a sub-agent (depth 1 session) cannot spawn further sub-agents
        var manager = CreateManager(supervisor.Object, maxDepth: 1);

        // Simulate a request from inside an already-spawned sub-agent session
        var subAgentParentSessionId = SessionId.ForSubAgent("root-session", "existing-sub");
        var request = CreateSpawnRequest(parentSessionId: subAgentParentSessionId);

        Func<Task> act = () => manager.SpawnAsync(request);

        (await act.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("depth");
    }

    [Fact]
    public async Task SpawnAsync_BelowMaxDepth_Succeeds()
    {
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        // MaxDepth = 2 means sub-agents can themselves spawn sub-agents (depth up to 2)
        var manager = CreateManager(supervisor.Object, maxDepth: 2);

        // A top-level session (depth 0) can spawn sub-agents
        var topLevelRequest = CreateSpawnRequest(parentSessionId: SessionId.From("root-session"));
        var result = await manager.SpawnAsync(topLevelRequest);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task SpawnAsync_MaxDepthZero_NeverEnforced()
    {
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        // MaxDepth = 0 means unlimited
        var manager = CreateManager(supervisor.Object, maxDepth: 0);

        // Even deeply nested sessions should spawn without error
        var deepSessionId = SessionId.From("root::subagent::a::subagent::b::subagent::c");
        var request = CreateSpawnRequest(parentSessionId: deepSessionId);

        var result = await manager.SpawnAsync(request);
        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task SpawnAsync_TopLevelSession_MaxDepth1_Succeeds()
    {
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        // MaxDepth = 1: top-level sessions (depth 0) CAN spawn sub-agents
        var manager = CreateManager(supervisor.Object, maxDepth: 1);
        var request = CreateSpawnRequest(parentSessionId: SessionId.From("root-session"));

        var result = await manager.SpawnAsync(request);
        result.ShouldNotBeNull();
    }

    private static DefaultSubAgentManager CreateManager(IAgentSupervisor supervisor, int maxDepth)
    {
        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(It.IsAny<AgentId>()))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "openai"
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var options = new TestOptionsMonitor<GatewayOptions>(new GatewayOptions
        {
            SubAgents = new SubAgentOptions { MaxDepth = maxDepth }
        });

        return new DefaultSubAgentManager(
            supervisor,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            options,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest(SessionId? parentSessionId = null)
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = parentSessionId ?? SessionId.From("root-session"),
            Task = "Do something",
            TimeoutSeconds = 600
        };

    private static Mock<IAgentHandle> CreateHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }
}
