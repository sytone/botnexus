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
        store.Setup(value => value.GetAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Id.Value.ShouldBe("heartbeat:agent-a");
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
        store.Setup(value => value.GetAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);

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
            Id = JobId.From("heartbeat:agent-a"),
            Name = "Heartbeat \u2014 Agent A",
            Schedule = "*/30 * * * *",
            ActionType = "heartbeat",
            AgentId = AgentId.From("agent-a"),
            Message = "old",
            Enabled = true,
            System = true,
            CreatedBy = "system:heartbeat",
            CreatedAt = DateTimeOffset.UtcNow
        };
        CronJob? updated = null;

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        store.Setup(value => value.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => updated = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => (CronJob?)job);

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
            Id = JobId.From("heartbeat:agent-a"),
            Name = "Heartbeat \u2014 Agent A",
            Schedule = "*/30 * * * *",
            ActionType = "heartbeat",
            AgentId = AgentId.From("agent-a"),
            Message = "test",
            Enabled = true,
            System = true,
            CreatedBy = "system:heartbeat",
            CreatedAt = DateTimeOffset.UtcNow
        };

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        store.Setup(value => value.DeleteAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(value => value.DeleteAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>()), Times.Once);
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
        store.Setup(value => value.GetAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Schedule.ShouldBe("0 * * * *");
    }

    [Fact]
    public async Task StartAsync_WithActiveHours_BakesHoursIntoSchedule()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new HeartbeatAgentConfig
        {
            Enabled = true,
            IntervalMinutes = 30,
            ActiveHours = new ActiveHoursConfig { Start = "08:00", End = "23:00", Timezone = "America/Los_Angeles" }
        });
        CronJob? created = null;

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Schedule.ShouldBe("*/30 8-22 * * *");
        created.TimeZone.ShouldBe("America/Los_Angeles");
    }

    [Fact]
    public async Task StartAsync_InvalidActiveHours_SkipsProvisioning()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new HeartbeatAgentConfig
        {
            Enabled = true,
            IntervalMinutes = 30,
            ActiveHours = new ActiveHoursConfig { Start = "22:00", End = "06:00" }  // midnight-spanning — invalid
        });

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_ActiveHoursChanged_UpdatesJobScheduleAndTimezone()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new HeartbeatAgentConfig
        {
            Enabled = true,
            IntervalMinutes = 30,
            ActiveHours = new ActiveHoursConfig { Start = "09:00", End = "18:00", Timezone = "Europe/London" }
        });
        var existing = new CronJob
        {
            Id = JobId.From("heartbeat:agent-a"),
            Name = "Heartbeat \u2014 Agent A",
            Schedule = "*/30 * * * *",
            ActionType = "heartbeat",
            AgentId = AgentId.From("agent-a"),
            Message = "Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK.",
            Enabled = true,
            System = true,
            TimeZone = null,
            CreatedBy = "system:heartbeat",
            CreatedAt = DateTimeOffset.UtcNow
        };
        CronJob? updated = null;

        registry.Setup(value => value.GetAll()).Returns([descriptor]);
        store.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(value => value.GetAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        store.Setup(value => value.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => updated = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => (CronJob?)job);

        var provisioner = new HeartbeatCronProvisioner(registry.Object, store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        updated.ShouldNotBeNull();
        updated!.Schedule.ShouldBe("*/30 9-17 * * *");
        updated.TimeZone.ShouldBe("Europe/London");
    }

    // --- BuildCronExpression unit tests ---

    [Theory]
    [InlineData(30, null, null, null, "*/30 * * * *")]
    [InlineData(60, null, null, null, "0 * * * *")]
    [InlineData(15, null, null, null, "*/15 * * * *")]
    [InlineData(120, null, null, null, "0 */2 * * *")]
    [InlineData(30, "08:00", "23:00", null, "*/30 8-22 * * *")]
    [InlineData(30, "08:00", "23:30", null, "*/30 8-23 * * *")]
    [InlineData(60, "09:00", "18:00", null, "0 9-17 * * *")]
    [InlineData(120, "08:00", "22:00", null, "0 8-21/2 * * *")]
    [InlineData(15, "00:00", "12:00", null, "*/15 0-11 * * *")]
    public void BuildCronExpression_Variants_ProduceExpectedExpression(
        int intervalMinutes, string? activeStart, string? activeEnd, string? tz, string expected)
    {
        var config = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = intervalMinutes };
        if (activeStart is not null)
        {
            config.ActiveHours = new ActiveHoursConfig { Start = activeStart, End = activeEnd!, Timezone = tz };
        }

        var result = HeartbeatCronProvisioner.BuildCronExpression(config);

        result.ShouldBe(expected);
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
