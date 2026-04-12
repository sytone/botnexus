using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class CrossAgentCallingTests
{
    [Fact]
    public async Task CrossAgentCall_RoutesThroughRegistrySupervisorAndIsolationStrategy()
    {
        var descriptor = CreateDescriptor("target-agent");
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("target-agent")).Returns(true);
        registry.Setup(r => r.Get("target-agent")).Returns(descriptor);

        var handle = CreateHandle("target-agent");
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("in-process");
        strategy.Setup(s => s.CreateAsync(
                descriptor,
                It.IsAny<AgentExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var supervisor = new DefaultAgentSupervisor(registry.Object, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor, NullLogger<DefaultAgentCommunicator>.Instance);

        await communicator.CallCrossAgentAsync("caller-agent", string.Empty, "target-agent", "hello");

        registry.Verify(r => r.Contains("target-agent"), Times.Once);
        registry.Verify(r => r.Get("target-agent"), Times.Once);
        strategy.Verify(s => s.CreateAsync(
            descriptor,
            It.Is<AgentExecutionContext>(ctx => ctx.SessionId.Value.StartsWith("xagent::caller-agent::target-agent", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CrossAgentCall_WhenRecursiveAtoBtoA_Throws()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains(It.IsAny<BotNexus.Domain.Primitives.AgentId>())).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        DefaultAgentCommunicator? communicator = null;
        supervisor
            .Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-b"), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await communicator!.CallCrossAgentAsync("agent-b", string.Empty, "agent-a", "loop");
                return CreateHandle("agent-b").Object;
            });
        communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("agent-a", string.Empty, "agent-b", "hello");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Recursive cross-agent call detected*");
    }

    [Fact]
    public async Task CrossAgentCall_WhenTargetNotRegistered_ThrowsKeyNotFound()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("missing-agent")).Returns(false);
        var communicator = new DefaultAgentCommunicator(registry.Object, Mock.Of<IAgentSupervisor>(), NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("caller-agent", string.Empty, "missing-agent", "hello");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CrossAgentCall_WhenTargetCreateFails_PropagatesFailure()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("target-agent")).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("target-agent"), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("create failed"));
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("caller-agent", string.Empty, "target-agent", "hello");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*create failed*");
    }

    [Fact]
    public async Task CrossAgentCall_UsesScopedSessionId()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("target-agent")).Returns(true);
        BotNexus.Domain.Primitives.SessionId? capturedSessionId = null;
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("target-agent"), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Callback<BotNexus.Domain.Primitives.AgentId, BotNexus.Domain.Primitives.SessionId, CancellationToken>((_, sessionId, _) => capturedSessionId = sessionId)
            .ReturnsAsync(CreateHandle("target-agent").Object);
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        await communicator.CallCrossAgentAsync("caller-agent", string.Empty, "target-agent", "hello");

        capturedSessionId!.Value.Value.Should().StartWith("xagent::caller-agent::target-agent");
    }

    [Fact]
    public async Task CrossAgentCall_WithConcurrency_CallsUseDistinctSessions()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("target-agent")).Returns(true);
        var seenSessions = new ConcurrentBag<string>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("target-agent"), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns((BotNexus.Domain.Primitives.AgentId _, BotNexus.Domain.Primitives.SessionId sessionId, CancellationToken _) =>
            {
                seenSessions.Add(sessionId.Value);
                var handle = new Mock<IAgentHandle>();
                handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AgentResponse { Content = sessionId.Value });
                return Task.FromResult(handle.Object);
            });
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => communicator.CallCrossAgentAsync("caller-agent", string.Empty, "target-agent", $"message-{i}")));

        seenSessions.Distinct(StringComparer.Ordinal).Should().HaveCount(16);
    }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = agentId,
            DisplayName = "Target Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };

    private static Mock<IAgentHandle> CreateHandle(string agentId)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns("session");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok" });
        return handle;
    }
}
