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

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = agentId,
            DisplayName = $"{agentId}-display",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };
}
