using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// Terminalizes cron sessions whose process ended before <see cref="CronTrigger"/> could run its
/// in-process finalizer. Reconciliation runs in the host's starting phase, before any hosted
/// service can begin new cron execution, so an Active persisted cron row belongs to a prior process.
/// </summary>
public sealed class CronSessionStartupReconciler(
    ISessionStore sessions,
    ILogger<CronSessionStartupReconciler> logger) : IHostedLifecycleService
{
    /// <summary>
    /// Seals Active cron rows from the prior process before hosted services start new work.
    /// Re-running this operation is safe because terminal rows are not written again.
    /// </summary>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var staleSessions = (await sessions.ListAsync(null, cancellationToken).ConfigureAwait(false))
            .Where(session => session.Status == SessionStatus.Active
                && session.ChannelType?.Value.Equals("cron", StringComparison.Ordinal) == true)
            .ToList();

        foreach (var session in staleSessions)
        {
            session.Status = SessionStatus.Sealed;
            await sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }

        if (staleSessions.Count > 0)
        {
            logger.LogWarning(
                "Terminalized {Count} stale Active cron sessions left by a previous gateway process.",
                staleSessions.Count);
        }
    }

    /// <summary>No StartAsync work is required because reconciliation precedes hosted-service startup.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No post-start work is required.</summary>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No pre-stop work is required; CronTrigger owns in-process finalization.</summary>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No shutdown work is required; CronTrigger owns in-process finalization.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No post-stop work is required.</summary>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
