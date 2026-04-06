using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class MaxConcurrentSessionsTests
{
    [Fact(Skip = "Awaiting max concurrent session enforcement in DefaultAgentSupervisor.")]
    public Task SupervisorRejectsSession_WhenMaxReached()
        => Task.CompletedTask;

    [Fact]
    public async Task SupervisorAllowsSession_WhenUnderLimit()
    {
        var supervisor = CreateSupervisor();

        var handle = await supervisor.GetOrCreateAsync("agent-a", "session-1");

        handle.Should().NotBeNull();
    }

    [Fact(Skip = "Awaiting max concurrent session configuration in DefaultAgentSupervisor.")]
    public Task SupervisorAllowsUnlimited_WhenMaxIsZero()
        => Task.CompletedTask;

    [Fact]
    public async Task SupervisorAllowsUnlimited_WhenMaxNotSet()
    {
        var supervisor = CreateSupervisor();

        var createTasks = Enumerable.Range(1, 20)
            .Select(i => supervisor.GetOrCreateAsync("agent-a", $"session-{i}"));
        await Task.WhenAll(createTasks);

        supervisor.GetAllInstances().Should().HaveCount(20);
    }

    private static DefaultAgentSupervisor CreateSupervisor()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = "agent-a",
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test"
        });

        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(
                It.IsAny<AgentDescriptor>(),
                It.IsAny<AgentExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDescriptor _, AgentExecutionContext context, CancellationToken _) =>
            {
                var handle = new Mock<IAgentHandle>();
                handle.SetupGet(h => h.AgentId).Returns("agent-a");
                handle.SetupGet(h => h.SessionId).Returns(context.SessionId);
                handle.Setup(h => h.IsRunning).Returns(false);
                return Task.FromResult(handle.Object);
            });

        return new DefaultAgentSupervisor(registry, [strategy.Object], NullLogger<DefaultAgentSupervisor>.Instance);
    }
}
