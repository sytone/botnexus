using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class DefaultAgentCommunicatorTests
{
    [Fact]
    public async Task CallSubAgentAsync_WithParentSession_CreatesScopedSessionId()
    {
        var registry = new Mock<IAgentRegistry>();
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();
        string? capturedSessionId = null;
        supervisor
            .Setup(s => s.GetOrCreateAsync("child-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, sessionId, _) => capturedSessionId = sessionId)
            .ReturnsAsync(handle.Object);
        handle.Setup(h => h.PromptAsync("hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok" });
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        await communicator.CallSubAgentAsync("parent-agent", "parent-session", "child-agent", "hello");

        capturedSessionId.Should().Be("parent-session::sub::child-agent");
    }

    [Fact]
    public async Task CallCrossAgentAsync_WhenLocalCall_RoutesThroughRegistrySupervisorAndIsolation()
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
                It.Is<AgentExecutionContext>(ctx => ctx.SessionId.StartsWith("caller-agent::cross::target-agent::", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var supervisor = new DefaultAgentSupervisor(registry.Object, [strategy.Object], NullLogger<DefaultAgentSupervisor>.Instance);
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor, NullLogger<DefaultAgentCommunicator>.Instance);

        var result = await communicator.CallCrossAgentAsync("caller-agent", string.Empty, "target-agent", "hello");

        result.Content.Should().Be("ok");
        registry.Verify(r => r.Contains("target-agent"), Times.Once);
        registry.Verify(r => r.Get("target-agent"), Times.Once);
        strategy.VerifyAll();
        handle.Verify(h => h.PromptAsync("hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CallCrossAgentAsync_WhenRecursiveChainDetected_ThrowsInvalidOperationException()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains(It.IsAny<string>())).Returns(true);

        var supervisor = new Mock<IAgentSupervisor>();
        DefaultAgentCommunicator? communicator = null;
        supervisor
            .Setup(s => s.GetOrCreateAsync("agent-b", It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
    public async Task CallCrossAgentAsync_WhenTargetAgentNotRegistered_ThrowsKeyNotFoundException()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("missing-agent")).Returns(false);
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("source-agent", string.Empty, "missing-agent", "hello");

        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("*missing-agent*");
        supervisor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CallCrossAgentAsync_WhenTargetCreateFails_PropagatesSupervisorError()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("target-agent")).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync("target-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("failed to create handle"));
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("source-agent", string.Empty, "target-agent", "hello");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*failed to create handle*");
    }

    [Fact]
    public async Task CallCrossAgentAsync_WhenLocalCall_UsesScopedCrossSessionId()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("target-agent")).Returns(true);
        var handle = CreateHandle("target-agent");
        string? capturedSessionId = null;
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync("target-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, sessionId, _) => capturedSessionId = sessionId)
            .ReturnsAsync(handle.Object);
        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        await communicator.CallCrossAgentAsync("caller-agent", string.Empty, "target-agent", "hello");

        capturedSessionId.Should().StartWith("caller-agent::cross::target-agent::");
    }

    [Fact]
    public async Task CallCrossAgentAsync_WithConcurrentCalls_UsesDistinctSessionsPerCall()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("target-agent")).Returns(true);

        var seenSessionIds = new ConcurrentBag<string>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync("target-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string sessionId, CancellationToken _) =>
            {
                seenSessionIds.Add(sessionId);
                var handle = new Mock<IAgentHandle>();
                handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AgentResponse { Content = sessionId });
                return Task.FromResult(handle.Object);
            });

        var communicator = new DefaultAgentCommunicator(registry.Object, supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);
        var calls = Enumerable.Range(0, 20)
            .Select(index => communicator.CallCrossAgentAsync("caller-agent", string.Empty, "target-agent", $"message-{index}"));

        var results = await Task.WhenAll(calls);

        seenSessionIds.Should().HaveCount(20);
        seenSessionIds.Distinct(StringComparer.Ordinal).Should().HaveCount(20);
        seenSessionIds.Should().OnlyContain(sessionId => sessionId.StartsWith("caller-agent::cross::target-agent::", StringComparison.Ordinal));
        results.Select(response => response.Content).Distinct(StringComparer.Ordinal).Should().HaveCount(20);
    }

    [Fact]
    public async Task CallCrossAgentAsync_WhenDepthExceedsConfiguredMaximum_ThrowsInvalidOperationException()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains(It.IsAny<string>())).Returns(true);

        var supervisor = new Mock<IAgentSupervisor>();
        DefaultAgentCommunicator? communicator = null;
        supervisor
            .Setup(s => s.GetOrCreateAsync("agent-b", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await communicator!.CallCrossAgentAsync("agent-b", string.Empty, "agent-c", "loop");
                return CreateHandle("agent-b").Object;
            });

        communicator = new DefaultAgentCommunicator(
            registry.Object,
            supervisor.Object,
            Options.Create(new GatewayOptions { MaxCallChainDepth = 1 }),
            NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("agent-a", string.Empty, "agent-b", "hello");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeded maximum configured depth*");
    }

    [Fact]
    public async Task CallCrossAgentAsync_WhenTargetTimesOut_ThrowsTimeoutException()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains("target-agent")).Returns(true);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new AgentResponse { Content = "never" };
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("target-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var communicator = new DefaultAgentCommunicator(
            registry.Object,
            supervisor.Object,
            Options.Create(new GatewayOptions { CrossAgentTimeoutSeconds = 1 }),
            NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("source-agent", string.Empty, "target-agent", "hello");

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*source-agent*target-agent*");
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
