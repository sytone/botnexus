using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Cron;

/// <summary>
/// Provisions or removes the heartbeat cron job for a single agent.
/// Called at startup (via <see cref="HeartbeatCronProvisioner"/>) and at runtime
/// when an agent is registered or updated via the API.
/// </summary>
public interface IHeartbeatProvisioner
{
    /// <summary>
    /// Ensures the heartbeat cron job for <paramref name="descriptor"/> is in sync
    /// with its current configuration. Creates, updates, or removes the job as needed.
    /// </summary>
    Task ProvisionAsync(AgentDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the heartbeat cron job belonging to <paramref name="agentId"/>.
    /// </summary>
    /// <remarks>
    /// #3524: agent delete previously called no provisioner at all, so every deleted agent left its
    /// <c>heartbeat:&lt;agentId&gt;</c> job behind to fire forever against an unregistered agent. A
    /// deleted agent has no descriptor left to pass, which is why <see cref="ProvisionAsync"/> could
    /// not serve the delete path and the missing call site was never obvious. Implementations must be
    /// idempotent (absent job is a no-op) and must not delete a job the platform does not own
    /// (<c>System == false</c>), matching the guard <see cref="ProvisionAsync"/> already applies.
    /// </remarks>
    Task DeprovisionAsync(AgentId agentId, CancellationToken cancellationToken);
}
