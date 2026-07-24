using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Cron;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #2065: agent create/update/delete must be failure-atomic across the runtime registry and the
/// persisted config. These tests inject failures at the persistence and provisioning boundaries
/// and assert that disk and runtime never diverge, and that incomplete descriptors are rejected
/// before they can clear persisted properties.
/// </summary>
public sealed class AgentLifecycleAtomicityTests
{
    private static AgentDescriptor Descriptor(string id, string display = "Display")
        => new()
        {
            AgentId = AgentId.From(id),
            DisplayName = display,
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

    private static Mock<IAgentChangeNotifier> Notifier()
    {
        var notifier = new Mock<IAgentChangeNotifier>();
        notifier.Setup(n => n.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return notifier;
    }

    // ── Register: persist-before-registry ─────────────────────────────────────

    [Fact]
    public async Task Register_WhenPersistFails_DoesNotCommitRegistry()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [Notifier().Object]);

        var result = await controller.Register(Descriptor("agent-a"), CancellationToken.None);

        // Persistence failed first, so the runtime registry must remain untouched.
        registry.Get(AgentId.From("agent-a")).ShouldBeNull();
        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task Register_WhenProvisionerFails_RollsBackRegistryAndConfig()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        writer.Setup(w => w.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var heartbeat = new Mock<IHeartbeatProvisioner>();
        heartbeat.Setup(p => p.ProvisionAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cron store offline"));
        var controller = new AgentsController(
            registry, Mock.Of<IAgentSupervisor>(), writer.Object, [Notifier().Object], heartbeat.Object);

        var result = await controller.Register(Descriptor("agent-a"), CancellationToken.None);

        // Provisioning failed after persist+register, so both must be rolled back.
        registry.Get(AgentId.From("agent-a")).ShouldBeNull();
        writer.Verify(w => w.DeleteAsync("agent-a", It.IsAny<CancellationToken>()), Times.Once);
        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task Register_WithIncompleteDescriptor_ReturnsBadRequestAndPersistsNothing()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [Notifier().Object]);

        // Blank DisplayName would clear the persisted display name on reload.
        var incomplete = Descriptor("agent-a", display: "   ");
        var result = await controller.Register(incomplete, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        registry.Get(AgentId.From("agent-a")).ShouldBeNull();
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Update: persist-before-registry + rollback ────────────────────────────

    [Fact]
    public async Task Update_WhenPersistFails_RegistryRetainsPreviousDescriptor()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor("agent-a", "Original"));
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [Notifier().Object]);

        var result = await controller.Update("agent-a", Descriptor("agent-a", "Updated"), CancellationToken.None);

        registry.Get(AgentId.From("agent-a"))!.DisplayName.ShouldBe("Original");
        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task Update_WhenProvisionerFails_RollsBackRegistryToPreviousDescriptor()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor("agent-a", "Original"));
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var heartbeat = new Mock<IHeartbeatProvisioner>();
        heartbeat.Setup(p => p.ProvisionAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cron store offline"));
        var controller = new AgentsController(
            registry, Mock.Of<IAgentSupervisor>(), writer.Object, [Notifier().Object], heartbeat.Object);

        var result = await controller.Update("agent-a", Descriptor("agent-a", "Updated"), CancellationToken.None);

        // Registry rolled back to the previous descriptor and config re-saved with it.
        registry.Get(AgentId.From("agent-a"))!.DisplayName.ShouldBe("Original");
        writer.Verify(w => w.SaveAsync(It.Is<AgentDescriptor>(d => d.DisplayName == "Original"), It.IsAny<CancellationToken>()), Times.Once);
        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task Update_WithIncompleteDescriptor_ReturnsBadRequestAndPersistsNothing()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor("agent-a", "Original"));
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [Notifier().Object]);

        var result = await controller.Update("agent-a", Descriptor("agent-a", "   "), CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        registry.Get(AgentId.From("agent-a"))!.DisplayName.ShouldBe("Original");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Unregister: delete config before dropping the registry ────────────────

    [Fact]
    public async Task Unregister_WhenConfigDeleteFails_RegistryRetainsAgent()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor("agent-a"));
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [Notifier().Object]);

        var result = await controller.Unregister("agent-a", CancellationToken.None);

        // Config delete failed first, so the agent must still be registered (no divergence).
        registry.Get(AgentId.From("agent-a")).ShouldNotBeNull();
        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task Unregister_WhenSuccessful_RemovesConfigAndRegistry()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor("agent-a"));
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, [Notifier().Object]);

        var result = await controller.Unregister("agent-a", CancellationToken.None);

        registry.Get(AgentId.From("agent-a")).ShouldBeNull();
        writer.Verify(w => w.DeleteAsync("agent-a", It.IsAny<CancellationToken>()), Times.Once);
        result.ShouldBeOfType<NoContentResult>();
    }
}
