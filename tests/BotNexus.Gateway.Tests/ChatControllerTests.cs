using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ChatControllerTests
{
    [Fact]
    public async Task Steer_WhenSessionMissing_ReturnsNotFound()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance("agent-a", "session-1")).Returns((AgentInstance?)null);
        var controller = new ChatController(supervisor.Object, Mock.Of<ISessionStore>());

        var result = await controller.Steer(new AgentControlRequest("agent-a", "session-1", "adjust"), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Steer_WhenSessionExists_QueuesSteeringMessage()
    {
        var handle = new Mock<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance("agent-a", "session-1"))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = "agent-a",
                SessionId = "session-1",
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var controller = new ChatController(supervisor.Object, Mock.Of<ISessionStore>());

        var result = await controller.Steer(new AgentControlRequest("agent-a", "session-1", "adjust"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        handle.Verify(h => h.SteerAsync("adjust", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FollowUp_WhenSessionExists_QueuesFollowUpMessage()
    {
        var handle = new Mock<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance("agent-a", "session-1"))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = "agent-a",
                SessionId = "session-1",
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var controller = new ChatController(supervisor.Object, Mock.Of<ISessionStore>());

        var result = await controller.FollowUp(new AgentControlRequest("agent-a", "session-1", "after this"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        handle.Verify(h => h.FollowUpAsync("after this", It.IsAny<CancellationToken>()), Times.Once);
    }
}
