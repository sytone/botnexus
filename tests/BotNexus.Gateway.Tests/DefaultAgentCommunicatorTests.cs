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
    public async Task CallCrossAgentAsync_WhenCalled_ThrowsNotImplementedException()
    {
        var communicator = new DefaultAgentCommunicator(Mock.Of<IAgentSupervisor>(), NullLogger<DefaultAgentCommunicator>.Instance);

        var act = () => communicator.CallCrossAgentAsync("a", "https://target", "b", "hello");

        await act.Should().ThrowAsync<NotImplementedException>();
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
}
