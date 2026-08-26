using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3524: deleting an agent must reclaim the system cron jobs the platform provisioned for it.
/// Before this, no deprovision method existed at all, so every deleted agent left a
/// <c>heartbeat:&lt;id&gt;</c> and <c>skill-review:&lt;id&gt;</c> job firing against an id the
/// registry no longer knows - the failure signature tracked in #3517.
/// </summary>
public sealed class ProvisionerDeprovisionTests
{
    // ── Heartbeat ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_DeprovisionAsync_WhenSystemJobExists_DeletesIt()
    {
        var store = StoreReturning(SystemJob("heartbeat:agent-a"));
        var provisioner = Heartbeat(store);

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        store.Verify(s => s.DeleteAsync(JobId.From("heartbeat:agent-a"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Heartbeat_DeprovisionAsync_WhenJobAbsent_IsNoOp()
    {
        var store = StoreReturning(null);
        var provisioner = Heartbeat(store);

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        // Idempotency: a second delete, or an agent that never had heartbeat enabled, must not
        // reach the store at all - and must not throw.
        store.Verify(s => s.DeleteAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Heartbeat_DeprovisionAsync_WhenJobIsNotSystem_LeavesItAlone()
    {
        var store = StoreReturning(SystemJob("heartbeat:agent-a") with { System = false });
        var provisioner = Heartbeat(store);

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        // Ownership guard, mirroring ProvisionAsync's disable branch: the platform only reclaims
        // jobs it minted. An operator-authored job sharing the id survives agent deletion.
        store.Verify(s => s.DeleteAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Skill review ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SkillReview_DeprovisionAsync_WhenSystemJobExists_DeletesIt()
    {
        var store = StoreReturning(SystemJob("skill-review:agent-a"));
        var provisioner = SkillReview(store);

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        store.Verify(s => s.DeleteAsync(JobId.From("skill-review:agent-a"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SkillReview_DeprovisionAsync_WhenJobAbsent_IsNoOp()
    {
        var store = StoreReturning(null);
        var provisioner = SkillReview(store);

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        store.Verify(s => s.DeleteAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SkillReview_DeprovisionAsync_WhenJobIsNotSystem_LeavesItAlone()
    {
        var store = StoreReturning(SystemJob("skill-review:agent-a") with { System = false });
        var provisioner = SkillReview(store);

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        store.Verify(s => s.DeleteAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Provision -> deprovision round trip ───────────────────────────────────

    [Fact]
    public async Task Heartbeat_ProvisionThenDeprovision_LeavesNoJobBehind()
    {
        await using var context = await TestInfrastructure.CronStoreTestContext.CreateAsync();
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);
        var provisioner = new HeartbeatCronProvisioner(
            registry.Object, context.Store, NullLogger<HeartbeatCronProvisioner>.Instance);
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Kind = AgentKind.Named,
            Heartbeat = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 30 }
        };

        await provisioner.ProvisionAsync(descriptor, CancellationToken.None);
        (await context.Store.GetAsync(JobId.From("heartbeat:agent-a"))).ShouldNotBeNull(
            "Provisioning must create the job, otherwise the deprovision assertion below is vacuous.");

        await provisioner.DeprovisionAsync(descriptor.AgentId, CancellationToken.None);

        (await context.Store.GetAsync(JobId.From("heartbeat:agent-a"))).ShouldBeNull(
            "The heartbeat job must be gone from the real store after deprovisioning.");
    }

    private static HeartbeatCronProvisioner Heartbeat(Mock<ICronStore> store)
        => new(Mock.Of<IAgentRegistry>(), store.Object, NullLogger<HeartbeatCronProvisioner>.Instance);

    private static SkillReviewCronProvisioner SkillReview(Mock<ICronStore> store)
        => new(Mock.Of<IAgentRegistry>(), store.Object, NullLogger<SkillReviewCronProvisioner>.Instance);

    private static Mock<ICronStore> StoreReturning(CronJob? job)
    {
        var store = new Mock<ICronStore>();
        store.Setup(s => s.GetAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>())).ReturnsAsync(job);
        store.Setup(s => s.DeleteAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return store;
    }

    private static CronJob SystemJob(string id)
        => new()
        {
            Id = JobId.From(id),
            Name = id,
            Schedule = "*/30 * * * *",
            ActionType = "heartbeat",
            AgentId = AgentId.From("agent-a"),
            Enabled = true,
            System = true,
            CreatedBy = "system",
            CreatedAt = DateTimeOffset.UtcNow
        };
}
