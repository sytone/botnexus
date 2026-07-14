using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Cron;

/// <summary>
/// Provisions or removes the default skill-review cron job for a single agent.
/// Called at startup (via <see cref="SkillReviewCronProvisioner"/>) and at runtime when an
/// agent is registered or updated via the API, so a user-defined agent gets an active
/// skill-review loop out of the box.
/// </summary>
public interface ISkillReviewProvisioner
{
    /// <summary>
    /// Ensures the skill-review cron job for <paramref name="descriptor"/> is in the right state.
    /// A user-defined agent (<see cref="AgentKind.Named"/> and not built-in) that has no
    /// skill-review job yet gets one created, enabled by default. Sub-agents and built-in agents
    /// are skipped. Once a job exists it is <b>not</b> overwritten - the user is free to change the
    /// schedule, thresholds, or disable it, and those edits survive subsequent provisioning passes.
    /// </summary>
    Task ProvisionAsync(AgentDescriptor descriptor, CancellationToken cancellationToken);
}
