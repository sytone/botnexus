using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
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
        var controller = CreateController(registry);

        var result = controller.List();

        ((result.Result as OkObjectResult)?.Value as IReadOnlyList<AgentDescriptor>).Count().ShouldBe(1);
    }

    [Fact]
    public void Get_WithUnknownAgent_ReturnsNotFound()
    {
        var controller = CreateController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance));

        var result = controller.Get("missing");

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Register_WithValidDescriptor_ReturnsCreated()
    {
        var controller = CreateController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance));

        var result = await controller.Register(CreateDescriptor("agent-a"), CancellationToken.None);

        result.ShouldBeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Register_WithDuplicateAgent_ReturnsConflict()
    {
        var controller = CreateController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance));
        _ = await controller.Register(CreateDescriptor("agent-a"), CancellationToken.None);

        var result = await controller.Register(CreateDescriptor("agent-a"), CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Update_WithMismatchedRouteAndPayloadAgentId_ReturnsBadRequest()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var controller = CreateController(registry);

        var result = await controller.Update("agent-a", CreateDescriptor("agent-b"), CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_WithEmptyPayloadAgentId_UsesRouteAgentId()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var controller = CreateController(registry);
        var payload = CreateDescriptor("agent-a") with { AgentId = default };

        var result = await controller.Update("agent-a", payload, CancellationToken.None);
        var updated = (result.Result as OkObjectResult)?.Value as AgentDescriptor;

        updated.ShouldNotBeNull();
        updated!.AgentId.Value.ShouldBe("agent-a");
    }

    [Fact]
    public async Task Register_WithValidDescriptor_PersistsConfiguration()
    {
        var writer = new Mock<IAgentConfigurationWriter>();
        var descriptor = CreateDescriptor("agent-a");
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            writer.Object);

        _ = await controller.Register(descriptor, CancellationToken.None);

        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.AgentId == descriptor.AgentId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_WhenSuccessful_PersistsConfiguration()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object);

        var result = await controller.Update("agent-a", CreateDescriptor("agent-a") with { DisplayName = "updated" }, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.AgentId == "agent-a" && d.DisplayName == "updated"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unregister_DeletesPersistedConfiguration()
    {
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            writer.Object);

        _ = await controller.Unregister("agent-a", CancellationToken.None);

        writer.Verify(w => w.DeleteAsync("agent-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetHealth_WithNoActiveInstances_ReturnsUnknown()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var controller = CreateController(registry);

        var result = await controller.GetHealth("agent-a", CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as AgentHealthResponse;
        response.ShouldNotBeNull();
        response!.Status.ShouldBe("unknown");
        response.InstanceCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetHealth_WithUnknownAgent_ReturnsNotFound()
    {
        var controller = CreateController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance));

        var result = await controller.GetHealth("missing", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
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
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("s1"),
                IsolationStrategy = "in-process"
            }
        ]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("s1");

        supervisor.As<IAgentHandleInspector>()
            .Setup(s => s.GetHandle(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("s1")))
            .Returns(handle.Object);

        var controller = CreateController(registry, supervisor.Object);

        var result = await controller.GetHealth("agent-a", CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as AgentHealthResponse;
        response.ShouldNotBeNull();
        response!.Status.ShouldBe("unknown");
        response.InstanceCount.ShouldBe(1);
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
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("s1"),
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
            .Setup(s => s.GetHandle(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("s1")))
            .Returns(handle.Object);

        var controller = CreateController(registry, supervisor.Object);

        var result = await controller.GetHealth("agent-a", CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as AgentHealthResponse;
        response.ShouldNotBeNull();
        response!.Status.ShouldBe("healthy");
        response.InstanceCount.ShouldBe(1);
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
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("s1"),
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
            .Setup(s => s.GetHandle(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("s1")))
            .Returns(handle.Object);

        var controller = CreateController(registry, supervisor.Object);

        var result = await controller.GetHealth("agent-a", CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as AgentHealthResponse;
        response.ShouldNotBeNull();
        response!.Status.ShouldBe("unhealthy");
        response.InstanceCount.ShouldBe(1);
    }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = agentId,
            DisplayName = $"{agentId}-display",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

    private static AgentsController CreateController(IAgentRegistry registry, IAgentSupervisor? supervisor = null)
        => new(registry, supervisor ?? Mock.Of<IAgentSupervisor>(), new NoOpAgentConfigurationWriter());
}
