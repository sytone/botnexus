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
}
