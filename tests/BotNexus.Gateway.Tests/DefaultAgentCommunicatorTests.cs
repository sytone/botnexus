using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class DefaultAgentCommunicatorTests
{
    [Fact]
    public async Task CallSubAgentAsync_WithParentSession_CreatesScopedSessionId()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();
        var response = new AgentResponse { Content = "ok" };
        string? capturedSessionId = null;
        supervisor
            .Setup(s => s.GetOrCreateAsync("child-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, sessionId, _) => capturedSessionId = sessionId)
            .ReturnsAsync(handle.Object);
        handle.Setup(h => h.PromptAsync("hello", It.IsAny<CancellationToken>())).ReturnsAsync(response);
        var communicator = new DefaultAgentCommunicator(supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        await communicator.CallSubAgentAsync("parent-agent", "parent-session", "child-agent", "hello");

        capturedSessionId.Should().Be("parent-session::sub::child-agent");
    }

    [Fact]
    public async Task CallSubAgentAsync_WhenCalled_DelegatesToSupervisorThenHandlePrompt()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();
        var expected = new AgentResponse { Content = "child-response" };
        supervisor
            .Setup(s => s.GetOrCreateAsync("child-agent", "parent-session::sub::child-agent", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        handle
            .Setup(h => h.PromptAsync("hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var communicator = new DefaultAgentCommunicator(supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        var result = await communicator.CallSubAgentAsync("parent-agent", "parent-session", "child-agent", "hello");

        result.Should().Be(expected);
        supervisor.Verify(s => s.GetOrCreateAsync("child-agent", "parent-session::sub::child-agent", It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync("hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CallSubAgentAsync_WithStructuredParentSession_UsesScopedChildSessionFormat()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();
        string? capturedSessionId = null;
        supervisor
            .Setup(s => s.GetOrCreateAsync("child", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, sessionId, _) => capturedSessionId = sessionId)
            .ReturnsAsync(handle.Object);
        handle.Setup(h => h.PromptAsync("ping", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "pong" });
        var communicator = new DefaultAgentCommunicator(supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        await communicator.CallSubAgentAsync("parent", "session-1::workspace", "child", "ping");

        capturedSessionId.Should().Be("session-1::workspace::sub::child");
    }

    [Fact]
    public async Task CallCrossAgentAsync_WithRemoteEndpoint_ThrowsNotSupportedException()
    {
        var communicator = new DefaultAgentCommunicator(Mock.Of<IAgentSupervisor>(), NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("a", "https://target", "b", "hello");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task CallCrossAgentAsync_WithLocalEndpoint_CreatesCrossSessionAndPromptsTargetAgent()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();
        string? capturedSessionId = null;
        var expected = new AgentResponse { Content = "ok" };
        supervisor
            .Setup(s => s.GetOrCreateAsync("target", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, sessionId, _) => capturedSessionId = sessionId)
            .ReturnsAsync(handle.Object);
        handle
            .Setup(h => h.PromptAsync("hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var communicator = new DefaultAgentCommunicator(supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        var result = await communicator.CallCrossAgentAsync("source", string.Empty, "target", "hello");

        result.Should().Be(expected);
        capturedSessionId.Should().NotBeNull();
        capturedSessionId.Should().StartWith("cross::source::target::");
        supervisor.Verify(s => s.GetOrCreateAsync("target", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync("hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CallSubAgentAsync_WhenSupervisorThrows_PropagatesKeyNotFoundException()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync("child-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("missing"));
        var communicator = new DefaultAgentCommunicator(supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallSubAgentAsync("parent-agent", "parent-session", "child-agent", "hello");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CallCrossAgentAsync_WhenTargetAgentIsUnregistered_PropagatesKeyNotFoundException()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync("target-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Agent target-agent is not registered."));
        var communicator = new DefaultAgentCommunicator(supervisor.Object, NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("source-agent", string.Empty, "target-agent", "hello");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact(Skip = "Pending recursion detection implementation for sub-agent self-calls.")]
    public async Task CallSubAgentAsync_WhenChildMatchesParent_RejectsRecursiveCall()
    {
        var communicator = new DefaultAgentCommunicator(Mock.Of<IAgentSupervisor>(), NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallSubAgentAsync("shared-agent", "session-1", "shared-agent", "hello");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact(Skip = "Pending recursion detection implementation for nested sub-agent cycles.")]
    public async Task CallSubAgentAsync_WhenParentSessionAlreadyTargetsChild_RejectsRecursiveCall()
    {
        var communicator = new DefaultAgentCommunicator(Mock.Of<IAgentSupervisor>(), NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallSubAgentAsync("parent-agent", "session-1::sub::child-agent", "child-agent", "hello");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
