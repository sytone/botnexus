using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class DefaultAgentSupervisorTests
{
    [Fact]
    public async Task GetOrCreateAsync_WithConcurrentSameSession_CreatesSingleHandle()
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
        var handle = CreateHandleMock("agent-a", "session-1");
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(40);
                return handle.Object;
            });
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], NullLogger<DefaultAgentSupervisor>.Instance);

        var tasks = Enumerable.Range(0, 25)
            .Select(_ => supervisor.GetOrCreateAsync("agent-a", "session-1"));
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(h => ReferenceEquals(h, handle.Object));
        strategy.Verify(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.Is<AgentExecutionContext>(c => c.SessionId == "session-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithConcurrentDifferentSessions_CreatesPerSession()
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
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns((AgentDescriptor _, AgentExecutionContext context, CancellationToken _) => Task.FromResult(CreateHandleMock("agent-a", context.SessionId).Object));
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], NullLogger<DefaultAgentSupervisor>.Instance);

        await Task.WhenAll(
            supervisor.GetOrCreateAsync("agent-a", "session-1"),
            supervisor.GetOrCreateAsync("agent-a", "session-2"));

        strategy.Verify(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenMaxConcurrentSessionsReached_ThrowsLimitException()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = "agent-a",
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test",
            MaxConcurrentSessions = 1
        });

        var firstHandle = CreateHandleMock("agent-a", "session-1");
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstHandle.Object);
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], NullLogger<DefaultAgentSupervisor>.Instance);

        await supervisor.GetOrCreateAsync("agent-a", "session-1");
        var act = () => supervisor.GetOrCreateAsync("agent-a", "session-2");

        await act.Should().ThrowAsync<AgentConcurrencyLimitExceededException>()
            .WithMessage("*MaxConcurrentSessions (1)*");
    }

    private static Mock<IAgentHandle> CreateHandleMock(string agentId, string sessionId)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns(sessionId);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok" });
        handle.Setup(h => h.StreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());
        return handle;
    }

    private static async IAsyncEnumerable<AgentStreamEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
