using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ChatControllerTests
{
    [Fact]
    public async Task Steer_WhenSessionMissing_ReturnsNotFound()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"))).Returns((AgentInstance?)null);
        var controller = new ChatController(supervisor.Object, Mock.Of<ISessionStore>());

        var result = await controller.Steer(new AgentControlRequest("agent-a", "session-1", "adjust"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Steer_WhenSessionExists_QueuesSteeringMessage()
    {
        var handle = new Mock<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1")))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var controller = new ChatController(supervisor.Object, Mock.Of<ISessionStore>());

        var result = await controller.Steer(new AgentControlRequest("agent-a", "session-1", "adjust"), CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        handle.Verify(h => h.SteerAsync("adjust", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FollowUp_WhenSessionExists_QueuesFollowUpMessage()
    {
        var handle = new Mock<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1")))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var controller = new ChatController(supervisor.Object, Mock.Of<ISessionStore>());

        var result = await controller.FollowUp(new AgentControlRequest("agent-a", "session-1", "after this"), CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        handle.Verify(h => h.FollowUpAsync("after this", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_WhenAgentConcurrencyLimitReached_ReturnsTooManyRequests()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AgentConcurrencyLimitExceededException("agent-a", 1));

        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
            });

        var controller = new ChatController(supervisor.Object, sessionStore.Object);

        var result = await controller.Send(new ChatRequest("agent-a", "hello"), CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task Send_WhenAgentIsUnknown_ReturnsNotFound()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("missing-agent"), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Agent 'missing-agent' is not registered."));
        var sessionStore = new Mock<ISessionStore>();
        var controller = new ChatController(supervisor.Object, sessionStore.Object);

        var result = await controller.Send(new ChatRequest("missing-agent", "hello"), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
        sessionStore.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_WhenMessageIsEmpty_ReturnsBadRequest()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var sessionStore = new Mock<ISessionStore>();
        var controller = new ChatController(supervisor.Object, sessionStore.Object);

        var result = await controller.Send(new ChatRequest("agent-a", ""), CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        sessionStore.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
