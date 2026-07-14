using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Cron.Actions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron;

/// <summary>
/// Auto-provisions a default-enabled skill-review cron job for every <b>user-defined</b> agent, so
/// the periodic skill-review loop (<see cref="SkillReviewCronAction"/>) runs out of the box without
/// the operator having to hand-author a cron job in config.json.
/// </summary>
/// <remarks>
/// <para>
/// Sibling to <see cref="HeartbeatCronProvisioner"/>. Runs once at startup over the whole registry
/// and is also invoked per-agent at runtime from the agents API when an agent is registered or
/// updated (via <see cref="ISkillReviewProvisioner"/>).
/// </para>
/// <para>
/// <b>Scope - user-defined agents only.</b> A job is provisioned only for agents with
/// <see cref="AgentKind.Named"/> that are not built-in. Runtime-spawned sub-agents
/// (<see cref="AgentKind.SubAgent"/>) and built-in agents (<see cref="AgentDescriptor.IsBuiltIn"/>)
/// are skipped - sub-agents are ephemeral and have no configuration persistence, and built-ins are
/// platform infrastructure the user did not create.
/// </para>
/// <para>
/// <b>Non-destructive.</b> Unlike the heartbeat provisioner (which force-resyncs schedule/prompt on
/// every pass), this creates the job only when absent and then leaves it alone. A user is free to
/// change the schedule, thresholds (job metadata), or disable the job, and those edits survive
/// subsequent provisioning passes. To turn the loop off for an agent, disable the job rather than
/// delete it (a deleted job would be recreated on the next startup, matching heartbeat semantics).
/// </para>
/// </remarks>
public sealed class SkillReviewCronProvisioner : IHostedService, ISkillReviewProvisioner
{
    /// <summary>Default schedule: daily at 04:00, off-hours, staggered after memory-dreaming.</summary>
    private const string DefaultSchedule = "0 4 * * *";

    private readonly IAgentRegistry _registry;
    private readonly ICronStore _cronStore;
    private readonly ILogger<SkillReviewCronProvisioner> _logger;

    public SkillReviewCronProvisioner(
        IAgentRegistry registry,
        ICronStore cronStore,
        ILogger<SkillReviewCronProvisioner> logger)
    {
        _registry = registry;
        _cronStore = cronStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _cronStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        foreach (var descriptor in _registry.GetAll())
        {
            await ProvisionAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Returns <c>true</c> when the agent is a first-class, user-defined agent that should get a
    /// skill-review job: a <see cref="AgentKind.Named"/> agent that is not built-in.
    /// </summary>
    public static bool IsEligible(AgentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Kind == AgentKind.Named && !descriptor.IsBuiltIn;
    }

    /// <inheritdoc/>
    public async Task ProvisionAsync(AgentDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!IsEligible(descriptor))
        {
            _logger.LogDebug(
                "Skill-review provisioning skipped for agent '{AgentId}': not a user-defined agent (kind={Kind}, builtin={BuiltIn}).",
                descriptor.AgentId, descriptor.Kind, descriptor.IsBuiltIn);
            return;
        }

        var jobId = JobId.From($"skill-review:{descriptor.AgentId.Value}");

        var existing = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Non-destructive: never overwrite a job that already exists so user edits
            // (schedule / thresholds / disabled) survive every provisioning pass.
            _logger.LogDebug(
                "Skill-review cron job already present for agent '{AgentId}'; leaving user configuration untouched.",
                descriptor.AgentId);
            return;
        }

        var job = new CronJob
        {
            Id = jobId,
            Name = $"Skill Review \u2014 {descriptor.DisplayName}",
            Schedule = DefaultSchedule,
            ActionType = SkillReviewCronAction.TypeName,
            AgentId = descriptor.AgentId,
            Enabled = true,
            System = true,
            CreatedBy = "system:skill-review",
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["minToolCalls"] = 5,
                ["lookbackHours"] = 24,
                ["maxSessions"] = 50
            }
        };

        await _cronStore.CreateAsync(job, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Provisioned default skill-review cron job for agent '{AgentId}' with schedule '{Schedule}'.",
            descriptor.AgentId, DefaultSchedule);
    }
}
