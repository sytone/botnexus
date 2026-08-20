using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Plugins.Cron;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the platform-wide plugin-update job provisioner (#2683).
/// </summary>
/// <remarks>
/// The load-bearing test is the idempotency one: it edits the schedule the way a user would and
/// then re-runs provisioning, asserting the USER'S value survives. A test that merely re-ran
/// provisioning and counted jobs would pass for an implementation that overwrites the row with
/// identical defaults - which is precisely the regression that silently discards a user's edit.
/// </remarks>
public sealed class PluginUpdateCronProvisionerTests
{
    // AC2 - the job is provisioned on first plugin install, platform-wide (AgentId = null).
    [Fact]
    public async Task ProvisionsOnePlatformWideJobWithNoAgentId()
    {
        var store = new FakeCronStore();
        var provisioner = new PluginUpdateCronProvisioner(store);

        await provisioner.ProvisionAsync(CancellationToken.None);

        var job = await store.GetAsync(JobId.From(PluginUpdateCronProvisioner.PlatformJobId));
        Assert.NotNull(job);

        // Platform-wide, not per-agent: AgentId must be null. A job carrying an agent id would
        // bond a session and cost a model turn - the entire point of slice 4 is that it does not.
        Assert.Null(job!.AgentId);
        Assert.Equal(PluginUpdateCronAction.TypeName, job.ActionType);
        Assert.True(job.Enabled);
        Assert.True(job.System);
        Assert.Equal(1, store.CreateCallCount);
    }

    // AC3 - a job already present is left completely untouched, including a user-edited schedule.
    [Fact]
    public async Task AUserEditedScheduleSurvivesRepeatedProvisioning()
    {
        var store = new FakeCronStore();
        var provisioner = new PluginUpdateCronProvisioner(store);
        var jobId = JobId.From(PluginUpdateCronProvisioner.PlatformJobId);

        await provisioner.ProvisionAsync(CancellationToken.None);

        var provisioned = await store.GetAsync(jobId);
        Assert.NotNull(provisioned);

        // The user re-schedules the job to weekly, and disables it - two edits a provisioner that
        // "force-resyncs" would silently revert.
        const string userSchedule = "0 6 * * 1";
        Assert.NotEqual(userSchedule, provisioned!.Schedule);
        await store.UpdateDefinitionAsync(provisioned with { Schedule = userSchedule, Enabled = false });

        await provisioner.ProvisionAsync(CancellationToken.None);
        await provisioner.ProvisionAsync(CancellationToken.None);

        var after = await store.GetAsync(jobId);
        Assert.NotNull(after);
        Assert.Equal(userSchedule, after!.Schedule);
        Assert.False(after.Enabled);

        // And nothing was created a second time - idempotency is structural, not a coincidence of
        // the store deduplicating by id.
        Assert.Equal(1, store.CreateCallCount);
    }

    /// <summary>
    /// The job id is a fixed platform constant, not derived from an agent, so exactly one job can
    /// ever exist for the whole gateway.
    /// </summary>
    [Fact]
    public void PlatformJobIdIsAFixedConstant()
    {
        Assert.Equal("plugin-update", PluginUpdateCronProvisioner.PlatformJobId);
    }
}
