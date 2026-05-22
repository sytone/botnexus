using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class MemoryDreamingProvisionerTests
{
    [Fact]
    public async Task StartAsync_AgentWithDreamingEnabled_CreatesCronJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new MemoryDreamingConfig { Enabled = true, Schedule = "0 3 * * *" });
        CronJob? created = null;

        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        store.Setup(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new MemoryDreamingProvisioner(registry.Object, store.Object, NullLogger<MemoryDreamingProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Id.ShouldBe("memory-dreaming:agent-a");
        created.Schedule.ShouldBe("0 3 * * *");
        created.ActionType.ShouldBe("agent-prompt");
        created.System.ShouldBeTrue();
        created.CreatedBy.ShouldBe("system:memory-dreaming");
    }

    [Fact]
    public async Task StartAsync_AgentWithDreamingDisabled_DoesNotCreateJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new MemoryDreamingConfig { Enabled = false });

        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);

        var provisioner = new MemoryDreamingProvisioner(registry.Object, store.Object, NullLogger<MemoryDreamingProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_AgentWithNoDreamingConfig_DoesNotCreateJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", memoryDreaming: null);

        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);

        var provisioner = new MemoryDreamingProvisioner(registry.Object, store.Object, NullLogger<MemoryDreamingProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_ExistingJobWithChangedSchedule_UpdatesJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new MemoryDreamingConfig { Enabled = true, Schedule = "0 2 * * *" });
        var existing = new CronJob
        {
            Id = "memory-dreaming:agent-a",
            Name = "Memory Dreaming \u2014 Agent A",
            Schedule = "0 3 * * *",
            ActionType = "agent-prompt",
            AgentId = "agent-a",
            Message = "old",
            Enabled = true,
            System = true,
            CreatedBy = "system:memory-dreaming",
            CreatedAt = DateTimeOffset.UtcNow
        };
        CronJob? updated = null;

        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        store.Setup(s => s.UpdateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => updated = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new MemoryDreamingProvisioner(registry.Object, store.Object, NullLogger<MemoryDreamingProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        updated.ShouldNotBeNull();
        updated!.Schedule.ShouldBe("0 2 * * *");
    }

    [Fact]
    public async Task StartAsync_DreamingDisabled_RemovesExistingSystemJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new MemoryDreamingConfig { Enabled = false });
        var existing = new CronJob
        {
            Id = "memory-dreaming:agent-a",
            Name = "Memory Dreaming — Agent A",
            Schedule = "0 3 * * *",
            ActionType = "agent-prompt",
            System = true,
            CreatedBy = "system:memory-dreaming"
        };

        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        store.Setup(s => s.DeleteAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var provisioner = new MemoryDreamingProvisioner(registry.Object, store.Object, NullLogger<MemoryDreamingProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(s => s.DeleteAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_CustomPrompt_UsesCustomPrompt()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = CreateDescriptor("agent-a", new MemoryDreamingConfig
        {
            Enabled = true,
            Prompt = "My custom consolidation prompt."
        });
        CronJob? created = null;

        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        store.Setup(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new MemoryDreamingProvisioner(registry.Object, store.Object, NullLogger<MemoryDreamingProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        created!.Message.ShouldBe("My custom consolidation prompt.");
    }

    [Theory]
    [InlineData(1, "1 day")]
    [InlineData(7, "7 days")]
    [InlineData(14, "14 days")]
    public void BuildDefaultPrompt_IncludesLookbackDays(int days, string expected)
    {
        var prompt = MemoryDreamingProvisioner.BuildDefaultPrompt(days);
        prompt.ShouldContain(expected);
    }

    [Fact]
    public async Task StartAsync_ExistingJobMatchesConfig_DoesNotUpdate()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var config = new MemoryDreamingConfig { Enabled = true, Schedule = "0 3 * * *" };
        var descriptor = CreateDescriptor("agent-a", config);
        var expectedPrompt = MemoryDreamingProvisioner.BuildDefaultPrompt(config.LookbackDays);
        var existing = new CronJob
        {
            Id = "memory-dreaming:agent-a",
            Name = "Memory Dreaming — Agent A",
            Schedule = "0 3 * * *",
            ActionType = "agent-prompt",
            Message = expectedPrompt,
            Enabled = true,
            System = true,
            TimeZone = null
        };

        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync("memory-dreaming:agent-a", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var provisioner = new MemoryDreamingProvisioner(registry.Object, store.Object, NullLogger<MemoryDreamingProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(s => s.UpdateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static AgentDescriptor CreateDescriptor(string agentId, MemoryDreamingConfig? memoryDreaming)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            MemoryDreaming = memoryDreaming
        };
}
