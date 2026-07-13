using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
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

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        var agents = okResult.Value.ShouldBeAssignableTo<IReadOnlyList<AgentDescriptor>>();
        agents.ShouldNotBeNull();
        var registeredAgents = agents ?? throw new InvalidOperationException("Expected agent list.");
        registeredAgents.Count.ShouldBe(1);
    }

    [Fact]
    public void List_ByDefault_ExcludesSubAgentsAndBuiltins()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("user-agent"));
        registry.Register(CreateSubAgentDescriptor("parent--subagent--coder--abc"));
        registry.Register(CreateBuiltinDescriptor("coder"));
        var controller = CreateController(registry);

        var agents = ExtractAgents(controller.List());

        agents.Count.ShouldBe(1);
        agents.ShouldContain(a => a.AgentId == "user-agent");
        agents.ShouldNotContain(a => a.Kind == AgentKind.SubAgent);
        agents.ShouldNotContain(a => a.IsBuiltIn);
    }

    [Fact]
    public void List_WithIncludeSubAgents_IncludesSubAgentsButNotBuiltins()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("user-agent"));
        registry.Register(CreateSubAgentDescriptor("parent--subagent--coder--abc"));
        registry.Register(CreateBuiltinDescriptor("coder"));
        var controller = CreateController(registry);

        var agents = ExtractAgents(controller.List(includeSubAgents: true));

        agents.Count.ShouldBe(2);
        agents.ShouldContain(a => a.Kind == AgentKind.SubAgent);
        agents.ShouldNotContain(a => a.IsBuiltIn);
    }

    [Fact]
    public void List_WithIncludeBuiltin_IncludesBuiltinsButNotSubAgents()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("user-agent"));
        registry.Register(CreateSubAgentDescriptor("parent--subagent--coder--abc"));
        registry.Register(CreateBuiltinDescriptor("coder"));
        var controller = CreateController(registry);

        var agents = ExtractAgents(controller.List(includeBuiltin: true));

        agents.Count.ShouldBe(2);
        agents.ShouldContain(a => a.IsBuiltIn);
        agents.ShouldNotContain(a => a.Kind == AgentKind.SubAgent);
    }

    [Fact]
    public void List_WithBothIncludes_ReturnsEverything()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("user-agent"));
        registry.Register(CreateSubAgentDescriptor("parent--subagent--coder--abc"));
        registry.Register(CreateBuiltinDescriptor("coder"));
        var controller = CreateController(registry);

        var agents = ExtractAgents(controller.List(includeSubAgents: true, includeBuiltin: true));

        agents.Count.ShouldBe(3);
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
    public async Task Register_WhenSuccessful_IsImmediatelyVisibleViaListAndGet()
    {
        var controller = CreateController(new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance));

        _ = await controller.Register(CreateDescriptor("agent-a"), CancellationToken.None);
        var listResult = controller.List();
        var getResult = controller.Get("agent-a");

        var listOk = listResult.Result.ShouldBeOfType<OkObjectResult>();
        var agents = listOk.Value.ShouldBeAssignableTo<IReadOnlyList<AgentDescriptor>>();
        agents.ShouldNotBeNull();
        agents.ShouldContain(agent => agent.AgentId == "agent-a");
        var getOk = getResult.Result.ShouldBeOfType<OkObjectResult>();
        var descriptor = getOk.Value.ShouldBeOfType<AgentDescriptor>();
        descriptor.DisplayName.ShouldBe("agent-a-display");
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

    // Update_WithEmptyPayloadAgentId_UsesRouteAgentId was removed: AgentId is now a Vogen value
    // object that cannot be default. The "empty payload AgentId" branch (which the route param
    // backfilled) is no longer reachable; if a client wants to update an agent they must send
    // the AgentId in the payload, otherwise the JSON deserializer or AgentId.From throws first.
    // The route-vs-payload mismatch path (Update_WithMismatchedAgentIds_ReturnsBadRequest) still
    // covers the meaningful behaviour.

     [Fact]
    public async Task Register_WithValidDescriptor_PersistsConfiguration()
    {
        var writer = new Mock<IAgentConfigurationWriter>();
        var descriptor = CreateDescriptor("agent-a");
        var notifier = CreateNotifier();
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            writer.Object,
            [notifier.Object]);

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
        var notifier = CreateNotifier();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [notifier.Object]);

        var result = await controller.Update("agent-a", CreateDescriptor("agent-a") with { DisplayName = "updated" }, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.AgentId == "agent-a" && d.DisplayName == "updated"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_WhenSuccessful_ReflectsUpdatedDescriptorInListAndGet()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var notifier = CreateNotifier();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), new NoOpAgentConfigurationWriter(), [notifier.Object]);
        var updatedDescriptor = CreateDescriptor("agent-a") with
        {
            DisplayName = "updated-display",
            ModelId = "updated-model",
            ApiProvider = "updated-provider"
        };

        var updateResult = await controller.Update("agent-a", updatedDescriptor, CancellationToken.None);
        var listResult = controller.List();
        var getResult = controller.Get("agent-a");

        updateResult.Result.ShouldBeOfType<OkObjectResult>();
        var listOk = listResult.Result.ShouldBeOfType<OkObjectResult>();
        var agents = listOk.Value.ShouldBeAssignableTo<IReadOnlyList<AgentDescriptor>>();
        agents.ShouldNotBeNull();
        var listDescriptor = agents.Single(agent => agent.AgentId == "agent-a");
        listDescriptor.DisplayName.ShouldBe("updated-display");
        listDescriptor.ModelId.ShouldBe("updated-model");
        listDescriptor.ApiProvider.ShouldBe("updated-provider");
        var getOk = getResult.Result.ShouldBeOfType<OkObjectResult>();
        var getDescriptor = getOk.Value.ShouldBeOfType<AgentDescriptor>();
        getDescriptor.DisplayName.ShouldBe("updated-display");
        getDescriptor.ModelId.ShouldBe("updated-model");
        getDescriptor.ApiProvider.ShouldBe("updated-provider");
    }

    [Fact]
    public async Task Unregister_DeletesPersistedConfiguration()
    {
        var writer = new Mock<IAgentConfigurationWriter>();
        var notifier = CreateNotifier();
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            writer.Object,
            [notifier.Object]);

        _ = await controller.Unregister("agent-a", CancellationToken.None);

        writer.Verify(w => w.DeleteAsync("agent-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Register_WhenSuccessful_BroadcastsAgentsChangedAdded()
    {
        var descriptor = CreateDescriptor("agent-a");
        var notifier = CreateNotifier();
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object]);

        _ = await controller.Register(descriptor, CancellationToken.None);

        notifier.Verify(client => client.NotifyAgentsChangedAsync(
            "added",
            "agent-a",
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Register_WhenDuplicate_DoesNotBroadcastAgentsChanged()
    {
        var descriptor = CreateDescriptor("agent-a");
        var notifier = CreateNotifier();
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object]);

        _ = await controller.Register(descriptor, CancellationToken.None);
        _ = await controller.Register(descriptor, CancellationToken.None);

        notifier.Verify(client => client.NotifyAgentsChangedAsync(
            "added",
            "agent-a",
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Update_WhenSuccessful_BroadcastsAgentsChangedUpdated()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var notifier = CreateNotifier();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), new NoOpAgentConfigurationWriter(), [notifier.Object]);

        _ = await controller.Update("agent-a", CreateDescriptor("agent-a") with { DisplayName = "updated" }, CancellationToken.None);

        notifier.Verify(client => client.NotifyAgentsChangedAsync(
            "updated",
            "agent-a",
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Update_WhenBroadcastFails_ReturnsOkAndPersistsUpdate()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var writer = new Mock<IAgentConfigurationWriter>();
        var notifier = CreateNotifier();
        notifier.Setup(client => client.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("signalr failed"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [notifier.Object]);

        var result = await controller.Update("agent-a", CreateDescriptor("agent-a") with { DisplayName = "updated" }, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.AgentId == "agent-a" && d.DisplayName == "updated"),
            It.IsAny<CancellationToken>()), Times.Once);
        registry.Get(BotNexus.Domain.Primitives.AgentId.From("agent-a"))?.DisplayName.ShouldBe("updated");
    }

    [Fact]
    public async Task Update_WhenNotFound_DoesNotBroadcastAgentsChanged()
    {
        var notifier = CreateNotifier();
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object]);

        _ = await controller.Update("missing", CreateDescriptor("missing"), CancellationToken.None);

        notifier.Verify(client => client.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unregister_WhenAgentExists_BroadcastsAgentsChangedRemoved()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var notifier = CreateNotifier();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), new NoOpAgentConfigurationWriter(), [notifier.Object]);

        _ = await controller.Unregister("agent-a", CancellationToken.None);

        notifier.Verify(client => client.NotifyAgentsChangedAsync(
            "removed",
            "agent-a",
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Unregister_WhenBroadcastFails_ReturnsNoContentAndDeletesConfiguration()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var writer = new Mock<IAgentConfigurationWriter>();
        var notifier = CreateNotifier();
        notifier.Setup(client => client.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("signalr failed"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [notifier.Object]);

        var result = await controller.Unregister("agent-a", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        writer.Verify(w => w.DeleteAsync("agent-a", It.IsAny<CancellationToken>()), Times.Once);
        registry.Get(BotNexus.Domain.Primitives.AgentId.From("agent-a")).ShouldBeNull();
    }

    [Fact]
    public async Task Unregister_WhenAgentMissing_DoesNotBroadcastAgentsChanged()
    {
        var notifier = CreateNotifier();
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object]);

        _ = await controller.Unregister("missing", CancellationToken.None);

        notifier.Verify(client => client.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("s1"));

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
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("s1"));
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
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("s1"));
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
            AgentId = AgentId.From(agentId),
            DisplayName = $"{agentId}-display",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

    private static AgentDescriptor CreateSubAgentDescriptor(string agentId)
        => CreateDescriptor(agentId) with { Kind = AgentKind.SubAgent };

    private static AgentDescriptor CreateBuiltinDescriptor(string agentId)
        => CreateDescriptor(agentId) with
        {
            Metadata = new Dictionary<string, object?> { ["role"] = agentId, ["builtin"] = true }
        };

    private static IReadOnlyList<AgentDescriptor> ExtractAgents(
        ActionResult<IReadOnlyList<AgentDescriptor>> result)
    {
        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var agents = ok.Value.ShouldBeAssignableTo<IReadOnlyList<AgentDescriptor>>();
        return agents ?? throw new InvalidOperationException("Expected agent list.");
    }

    private static Mock<IAgentChangeNotifier> CreateNotifier()
    {
        var notifier = new Mock<IAgentChangeNotifier>();
        notifier.Setup(client => client.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return notifier;
    }

    private static AgentsController CreateController(IAgentRegistry registry, IAgentSupervisor? supervisor = null)
    {
        var notifier = CreateNotifier();
        return new AgentsController(
            registry,
            supervisor ?? Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object]);
    }

    // ── Heartbeat re-provisioning tests (issue #384) ──────────────────────────

    [Fact]
    public async Task Register_WithHeartbeatProvisioner_CallsProvisionAsync()
    {
        var provisioner = new Mock<IHeartbeatProvisioner>();
        provisioner
            .Setup(p => p.ProvisionAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var descriptor = CreateDescriptorWithHeartbeat("agent-hb");
        var notifier = CreateNotifier();
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object],
            provisioner.Object);

        _ = await controller.Register(descriptor, CancellationToken.None);

        provisioner.Verify(
            p => p.ProvisionAsync(
                It.Is<AgentDescriptor>(d => d.AgentId == "agent-hb"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Update_WithHeartbeatProvisioner_CallsProvisionAsync()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptorWithHeartbeat("agent-hb"));

        var provisioner = new Mock<IHeartbeatProvisioner>();
        provisioner
            .Setup(p => p.ProvisionAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var notifier = CreateNotifier();
        var controller = new AgentsController(
            registry,
            Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object],
            provisioner.Object);

        var updatedDescriptor = CreateDescriptorWithHeartbeat("agent-hb") with
        {
            Heartbeat = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 60 }
        };
        _ = await controller.Update("agent-hb", updatedDescriptor, CancellationToken.None);

        provisioner.Verify(
            p => p.ProvisionAsync(
                It.Is<AgentDescriptor>(d => d.AgentId == "agent-hb"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Register_WithoutHeartbeatProvisioner_SucceedsWithoutError()
    {
        var descriptor = CreateDescriptorWithHeartbeat("agent-hb");
        var notifier = CreateNotifier();
        // No IHeartbeatProvisioner injected — backwards-compatible default.
        var controller = new AgentsController(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance),
            Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object]);

        var result = await controller.Register(descriptor, CancellationToken.None);

        result.ShouldBeOfType<CreatedAtActionResult>();
    }

    private static AgentDescriptor CreateDescriptorWithHeartbeat(string agentId)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = $"{agentId}-display",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Heartbeat = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 30 }
        };

    // ── AgentKind REST guard tests (Phase 5 / F-6 part 1) ──────────────────────

    [Fact]
    public async Task Register_WithKindSubAgent_ReturnsBadRequest()
    {
        // SECURITY GUARD: a REST POST that attempts to register an agent with Kind = SubAgent
        // must be rejected at the controller. Sub-agents are runtime-only — only
        // DefaultSubAgentManager.SpawnAsync may stamp Kind = SubAgent on a descriptor.
        // If we accepted this, an attacker with REST access could either bypass the
        // spawn-tool deny gate or silently deprive a named agent of spawn_subagent.
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(
            registry,
            Mock.Of<IAgentSupervisor>(),
            writer.Object,
            [CreateNotifier().Object]);
        var descriptor = CreateDescriptor("attacker") with { Kind = BotNexus.Domain.World.AgentKind.SubAgent };

        var result = await controller.Register(descriptor, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>(
            "Register must reject Kind = SubAgent with 400 BadRequest. If this fails, the " +
            "REST-side sub-agent privilege guard is missing.");
        registry.Get(BotNexus.Domain.Primitives.AgentId.From("attacker")).ShouldBeNull(
            "Rejected descriptor must NOT have been written to the registry.");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never,
            "Rejected descriptor must NOT have been persisted to config.");
    }

    [Fact]
    public async Task Update_WithKindSubAgent_ReturnsBadRequest()
    {
        // Symmetric guard on the PUT path: a previously-Named agent must not be silently
        // converted to Kind = SubAgent through an Update payload. Same threat model as Register.
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("agent-a"));
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(
            registry,
            Mock.Of<IAgentSupervisor>(),
            writer.Object,
            [CreateNotifier().Object]);
        var updated = CreateDescriptor("agent-a") with { Kind = BotNexus.Domain.World.AgentKind.SubAgent };

        var result = await controller.Update("agent-a", updated, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>(
            "Update must reject Kind = SubAgent with 400 BadRequest. If this fails, an " +
            "attacker could convert a Named agent to SubAgent through PUT and deprive it " +
            "of spawn_subagent permanently.");
        registry.Get(BotNexus.Domain.Primitives.AgentId.From("agent-a"))!
            .Kind.ShouldBe(BotNexus.Domain.World.AgentKind.Named,
                "Rejected update must NOT have mutated the registered descriptor.");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never,
            "Rejected descriptor must NOT have been persisted to config.");
    }
}
