using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Verifies that <see cref="DefaultSubAgentManager"/> tolerates spawn requests whose
/// <c>maxTurns</c> / <c>timeoutSeconds</c> exceed the configured ceilings: the request is
/// clamped (not rejected) so a runaway budget cannot be smuggled in via a single spawn call.
/// The pure clamp arithmetic is covered directly in <c>SubAgentModelsTests</c>; these tests
/// assert the manager wires the clamp into the live spawn path without throwing.
/// </summary>
public sealed class SubAgentSpawnBudgetTests
{
    [Fact]
    public async Task SpawnAsync_TimeoutAboveMax_ClampsAndSucceeds()
    {
        var manager = CreateManager(out _);

        var request = CreateSpawnRequest() with { TimeoutSeconds = 999_999_999 };

        var result = await manager.SpawnAsync(request);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task SpawnAsync_MaxTurnsAboveCeiling_ClampsAndSucceeds()
    {
        var manager = CreateManager(out _);

        var request = CreateSpawnRequest() with { MaxTurns = 1_000_000 };

        var result = await manager.SpawnAsync(request);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task SpawnAsync_InRangeBudget_SucceedsUnchanged()
    {
        var manager = CreateManager(out _);

        var request = CreateSpawnRequest() with { MaxTurns = 10, TimeoutSeconds = 300 };

        var result = await manager.SpawnAsync(request);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
    }

    private static DefaultSubAgentManager CreateManager(out Mock<IAgentSupervisor> supervisor)
    {
        var childHandle = CreateHandle();
        supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

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
            SubAgents = new SubAgentOptions
            {
                MaxDepth = 1,
                MaxTurnsCeiling = 30,
                MaxTimeoutSeconds = 1800
            }
        });

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            options,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("root-session"),
            Task = "Do something",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
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
