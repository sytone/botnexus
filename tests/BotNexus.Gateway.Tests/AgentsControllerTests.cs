using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class AgentsControllerTests
{
    [Fact]
    public void List_WhenAgentsRegistered_ReturnsRegisteredAgents()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>());

        var result = controller.List();

        ((result.Result as OkObjectResult)?.Value as IReadOnlyList<AgentDescriptor>).Should().HaveCount(1);
    }

    [Fact]
    public void Get_WithUnknownAgent_ReturnsNotFound()
    {
        var controller = new AgentsController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance), Mock.Of<IAgentSupervisor>());

        var result = controller.Get("missing");

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Register_WithValidDescriptor_ReturnsCreated()
    {
        var controller = new AgentsController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance), Mock.Of<IAgentSupervisor>());

        var result = controller.Register(CreateDescriptor("agent-a"));

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public void Register_WithDuplicateAgent_ReturnsConflict()
    {
        var controller = new AgentsController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance), Mock.Of<IAgentSupervisor>());
        _ = controller.Register(CreateDescriptor("agent-a"));

        var result = controller.Register(CreateDescriptor("agent-a"));

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public void Update_WithMismatchedRouteAndPayloadAgentId_ReturnsBadRequest()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>());

        var result = controller.Update("agent-a", CreateDescriptor("agent-b"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Update_WithEmptyPayloadAgentId_UsesRouteAgentId()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>());
        var payload = CreateDescriptor("agent-a") with { AgentId = string.Empty };

        var result = controller.Update("agent-a", payload);
        var updated = (result.Result as OkObjectResult)?.Value as AgentDescriptor;

        updated.Should().NotBeNull();
        updated!.AgentId.Should().Be("agent-a");
    }

    [Fact]
    public async Task GetHealth_WithNoActiveInstances_ReturnsUnknown()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>());

        var result = await controller.GetHealth("agent-a", CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as AgentHealthResponse;
        response.Should().NotBeNull();
        response!.Status.Should().Be("unknown");
        response.InstanceCount.Should().Be(0);
    }

    [Fact]
    public async Task GetHealth_WithUnknownAgent_ReturnsNotFound()
    {
        var controller = new AgentsController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance), Mock.Of<IAgentSupervisor>());

        var result = await controller.GetHealth("missing", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetHealth_WithNonHealthCheckableHandle_ReturnsUnknown()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetAllInstances()).Returns([
            new AgentInstance
            {
                InstanceId = "agent-a::s1",
                AgentId = "agent-a",
                SessionId = "s1",
                IsolationStrategy = "in-process"
            }
        ]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("s1");

        supervisor.As<IAgentHandleInspector>()
            .Setup(s => s.GetHandle("agent-a", "s1"))
            .Returns(handle.Object);

        var controller = new AgentsController(registry, supervisor.Object);

        var result = await controller.GetHealth("agent-a", CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as AgentHealthResponse;
        response.Should().NotBeNull();
        response!.Status.Should().Be("unknown");
        response.InstanceCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHealth_WithHealthCheckableHandle_ReturnsHealthy()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetAllInstances()).Returns([
            new AgentInstance
            {
                InstanceId = "agent-a::s1",
                AgentId = "agent-a",
                SessionId = "s1",
                IsolationStrategy = "in-process"
            }
        ]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("s1");
        handle.As<IHealthCheckable>()
            .Setup(h => h.PingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        supervisor.As<IAgentHandleInspector>()
            .Setup(s => s.GetHandle("agent-a", "s1"))
            .Returns(handle.Object);

        var controller = new AgentsController(registry, supervisor.Object);

        var result = await controller.GetHealth("agent-a", CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as AgentHealthResponse;
        response.Should().NotBeNull();
        response!.Status.Should().Be("healthy");
        response.InstanceCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHealth_WithFailedHealthCheck_ReturnsUnhealthy()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetAllInstances()).Returns([
            new AgentInstance
            {
                InstanceId = "agent-a::s1",
                AgentId = "agent-a",
                SessionId = "s1",
                IsolationStrategy = "in-process"
            }
        ]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("s1");
        handle.As<IHealthCheckable>()
            .Setup(h => h.PingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        supervisor.As<IAgentHandleInspector>()
            .Setup(s => s.GetHandle("agent-a", "s1"))
            .Returns(handle.Object);

        var controller = new AgentsController(registry, supervisor.Object);

        var result = await controller.GetHealth("agent-a", CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as AgentHealthResponse;
        response.Should().NotBeNull();
        response!.Status.Should().Be("unhealthy");
        response.InstanceCount.Should().Be(1);
    }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = agentId,
            DisplayName = $"{agentId}-display",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };
}
