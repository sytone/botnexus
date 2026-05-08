using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class HeartbeatCronProvisionerTests
{
    [Fact]
    public async Task StartAsync_AgentWithHeartbeatEnabled_CreatesCronJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 30 });
        CronJob? created = null;

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync("heartbeat:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Id.ShouldBe("heartbeat:agent-a");
        created.Schedule.ShouldBe("*/30 * * * *");
        created.System.ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_AgentWithHeartbeatDisabled_DoesNotCreateJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new HeartbeatAgentConfig { Enabled = false });

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync("heartbeat:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_ExistingJobWithChangedInterval_UpdatesJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 15 });
        var existing = new CronJob
        {
            Id = "heartbeat:agent-a",
            Name = "Heartbeat — Agent A",
            Schedule = "*/30 * * * *",
            ActionType = "agent-prompt",
            AgentId = "agent-a",
            Message = "old",
            Enabled = true,
            System = true,
            CreatedBy = "system:heartbeat",
            CreatedAt = DateTimeOffset.UtcNow
        };
        CronJob? updated = null;

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync("heartbeat:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        store.Setup(value => value.UpdateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => updated = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        updated.ShouldNotBeNull();
        updated!.Schedule.ShouldBe("*/15 * * * *");
    }

    [Fact]
    public async Task StartAsync_HeartbeatDisabled_RemovesExistingSystemJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", null);
        var existing = new CronJob
        {
            Id = "heartbeat:agent-a",
            Name = "Heartbeat — Agent A",
            Schedule = "*/30 * * * *",
            ActionType = "agent-prompt",
            AgentId = "agent-a",
            Message = "test",
            Enabled = true,
            System = true,
            CreatedBy = "system:heartbeat",
            CreatedAt = DateTimeOffset.UtcNow
        };

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync("heartbeat:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        store.Setup(value => value.DeleteAsync("heartbeat:agent-a", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(value => value.DeleteAsync("heartbeat:agent-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_HourlyInterval_UsesCorrectCronExpression()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 60 });
        CronJob? created = null;

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync("heartbeat:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Schedule.ShouldBe("0 * * * *");
    }

    private static AgentDescriptor CreateDescriptor(string agentId, HeartbeatAgentConfig? heartbeat)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Heartbeat = heartbeat
        };
}
