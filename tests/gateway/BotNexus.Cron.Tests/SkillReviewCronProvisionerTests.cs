using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class SkillReviewCronProvisionerTests
{
    [Fact]
    public async Task StartAsync_UserDefinedAgent_CreatesEnabledSystemJob()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = Descriptor("agent-a");
        CronJob? created = null;

        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync(JobId.From("skill-review:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        store.Setup(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);

        var provisioner = new SkillReviewCronProvisioner(registry.Object, store.Object, NullLogger<SkillReviewCronProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Id.Value.ShouldBe("skill-review:agent-a");
        created.ActionType.ShouldBe("skill-review");
        created.AgentId.ShouldBe(AgentId.From("agent-a"));
        created.Enabled.ShouldBeTrue();
        created.System.ShouldBeTrue();
        created.Metadata.ShouldNotBeNull();
        created.Metadata!["enabled"].ShouldBe(true);
        created.Metadata["lookbackHours"].ShouldBe(24);
    }

    [Fact]
    public async Task StartAsync_SubAgent_IsSkipped()
    {
        var (registry, store) = MocksFor(Descriptor("sub-a", kind: AgentKind.SubAgent));

        var provisioner = new SkillReviewCronProvisioner(registry.Object, store.Object, NullLogger<SkillReviewCronProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_BuiltInAgent_IsSkipped()
    {
        var (registry, store) = MocksFor(Descriptor("builtin-a", builtIn: true));

        var provisioner = new SkillReviewCronProvisioner(registry.Object, store.Object, NullLogger<SkillReviewCronProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);

        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProvisionAsync_ExistingJob_IsNotOverwritten()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        var descriptor = Descriptor("agent-a");
        var existing = new CronJob
        {
            Id = JobId.From("skill-review:agent-a"),
            Name = "Skill Review \u2014 Agent A",
            Schedule = "0 9 * * 1",   // user changed the schedule
            ActionType = "skill-review",
            AgentId = AgentId.From("agent-a"),
            Enabled = false,            // user disabled it
            System = true,
            CreatedBy = "system:skill-review",
            CreatedAt = DateTimeOffset.UtcNow
        };

        store.Setup(s => s.GetAsync(JobId.From("skill-review:agent-a"), It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var provisioner = new SkillReviewCronProvisioner(registry.Object, store.Object, NullLogger<SkillReviewCronProvisioner>.Instance);

        await provisioner.ProvisionAsync(descriptor, CancellationToken.None);

        // Non-destructive: no create, no update - user edits survive.
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void IsEligible_Classifies()
    {
        SkillReviewCronProvisioner.IsEligible(Descriptor("a")).ShouldBeTrue();
        SkillReviewCronProvisioner.IsEligible(Descriptor("a", kind: AgentKind.SubAgent)).ShouldBeFalse();
        SkillReviewCronProvisioner.IsEligible(Descriptor("a", builtIn: true)).ShouldBeFalse();
    }

    private static (Mock<IAgentRegistry>, Mock<ICronStore>) MocksFor(AgentDescriptor descriptor)
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<ICronStore>();
        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>())).ReturnsAsync((CronJob?)null);
        return (registry, store);
    }

    private static AgentDescriptor Descriptor(string agentId, AgentKind kind = AgentKind.Named, bool builtIn = false)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Kind = kind,
            Metadata = builtIn
                ? new Dictionary<string, object?> { ["builtin"] = true }
                : new Dictionary<string, object?>()
        };
}
