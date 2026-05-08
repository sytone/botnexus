using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class MaxConcurrentSessionsTests
{
    [Fact]
    public async Task SupervisorRejectsSession_WhenMaxReached()
    {
        var supervisor = CreateSupervisor(maxConcurrentSessions: 1);

        await supervisor.GetOrCreateAsync("agent-a", "session-1");
        Func<Task> act = () => supervisor.GetOrCreateAsync("agent-a", "session-2");

        await act.ShouldThrowAsync<AgentConcurrencyLimitExceededException>();
    }

    [Fact]
    public async Task SupervisorAllowsSession_WhenUnderLimit()
    {
        var supervisor = CreateSupervisor();

        var handle = await supervisor.GetOrCreateAsync("agent-a", "session-1");

        handle.ShouldNotBeNull();
    }

    [Fact]
    public async Task SupervisorAllowsUnlimited_WhenMaxIsZero()
    {
        var supervisor = CreateSupervisor(maxConcurrentSessions: 0);

        var createTasks = Enumerable.Range(1, 20)
            .Select(i => supervisor.GetOrCreateAsync("agent-a", $"session-{i}"));
        await Task.WhenAll(createTasks);

        supervisor.GetAllInstances().Count().ShouldBe(20);
    }

    [Fact]
    public async Task SupervisorAllowsUnlimited_WhenMaxNotSet()
    {
        var supervisor = CreateSupervisor();

        var createTasks = Enumerable.Range(1, 20)
            .Select(i => supervisor.GetOrCreateAsync("agent-a", $"session-{i}"));
        await Task.WhenAll(createTasks);

        supervisor.GetAllInstances().Count().ShouldBe(20);
    }

    [Fact]
    public async Task SupervisorReusesExistingSession_WhenMaxReachedForAgent()
    {
        var supervisor = CreateSupervisor(maxConcurrentSessions: 1);

        var first = await supervisor.GetOrCreateAsync("agent-a", "session-1");
        var second = await supervisor.GetOrCreateAsync("agent-a", "session-1");

        second.ShouldBeSameAs(first);
    }

    private static DefaultAgentSupervisor CreateSupervisor(int? maxConcurrentSessions = null)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var descriptor = new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test",
            MaxConcurrentSessions = maxConcurrentSessions ?? 0
        };

        registry.Register(descriptor);

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

        return new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);
    }
}
