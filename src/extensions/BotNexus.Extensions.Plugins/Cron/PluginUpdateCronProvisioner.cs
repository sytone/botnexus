using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Plugins.Cron;

/// <summary>
/// Creates the single platform-wide <c>plugin-update</c> cron job the first time a plugin is
/// installed, and then never touches it again (#2683).
/// </summary>
/// <remarks>
/// <para>
/// <b>One job, not one per agent.</b> Sibling to <c>SkillReviewCronProvisioner</c> in
/// shape, but deliberately not in scope: plugins are installed into the gateway, not into an
/// agent, so a per-agent job would run the same update N times and race itself over one plugin
/// root. <c>CronJob.AgentId</c> is already nullable by design and the scheduler skips agentless
/// jobs cleanly in its session-rebonding pass, so no new "system job" concept is needed - the
/// platform-wide job is simply one with no agent.
/// </para>
/// <para>
/// <b>Provisioned on install, not at startup.</b> Startup is the wrong trigger for the same
/// reason the update check itself is not done at startup: a gateway that runs for months would
/// provision once and a gateway that never has a plugin would provision a job with nothing to do.
/// Install is the moment the job first becomes meaningful.
/// </para>
/// <para>
/// <b>Non-destructive, permanently.</b> The job is created only when absent. A user is free to
/// change the schedule or disable it, and those edits survive every subsequent install - which is
/// why this reads the store and returns early rather than upserting a canonical definition. To
/// turn the loop off, disable the job rather than delete it; a deleted job is recreated by the
/// next install, matching the skill-review and heartbeat semantics.
/// </para>
/// </remarks>
public sealed class PluginUpdateCronProvisioner : IPluginInstallObserver
{
    /// <summary>
    /// The fixed job id. A constant rather than a derived value because exactly one such job may
    /// exist for the whole gateway - the id IS the uniqueness guarantee.
    /// </summary>
    public const string PlatformJobId = "plugin-update";

    /// <summary>Default schedule: daily at 03:00, off-hours and staggered before skill review.</summary>
    internal const string DefaultSchedule = "0 3 * * *";

    private readonly ICronStore _cronStore;
    private readonly ILogger<PluginUpdateCronProvisioner>? _logger;

    /// <summary>Creates a provisioner over the cron store.</summary>
    /// <param name="cronStore">Store the platform job is created in.</param>
    /// <param name="logger">Logger, optional.</param>
    public PluginUpdateCronProvisioner(ICronStore cronStore, ILogger<PluginUpdateCronProvisioner>? logger = null)
    {
        _cronStore = cronStore ?? throw new ArgumentNullException(nameof(cronStore));
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task OnPluginInstalledAsync(CancellationToken cancellationToken = default) =>
        ProvisionAsync(cancellationToken);

    /// <summary>
    /// Creates the platform-wide job if it is absent, and does nothing at all if it is present.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        await _cronStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var jobId = JobId.From(PlatformJobId);

        var existing = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Never overwrite an existing job, so user edits (schedule / disabled) survive every
            // provisioning pass. This early return is the whole of AC3.
            _logger?.LogDebug(
                "Plugin-update cron job already present; leaving user configuration untouched.");
            return;
        }

        var job = new CronJob
        {
            Id = jobId,
            Name = "Plugin Updates",
            Schedule = DefaultSchedule,
            ActionType = PluginUpdateCronAction.TypeName,

            // Platform-wide: no agent, therefore no session bond and no model cost.
            AgentId = null,
            Enabled = true,
            System = true,
            CreatedBy = "system:plugin-update",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _cronStore.CreateAsync(job, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation(
            "Provisioned the platform-wide plugin-update cron job with schedule '{Schedule}'.",
            DefaultSchedule);
    }
}
