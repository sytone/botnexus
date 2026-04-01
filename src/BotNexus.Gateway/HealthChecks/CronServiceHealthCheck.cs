using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.HealthChecks;

/// <summary>
/// Health check for the cron scheduler. Reports healthy when the tick loop is running,
/// degraded when any jobs have 3+ consecutive failures, and unhealthy when the tick loop has stopped.
/// </summary>
public sealed class CronServiceHealthCheck(ICronService cronService, IOptions<CronConfig> options) : IHealthCheck
{
    private const int ConsecutiveFailureThreshold = 3;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cronConfig = options.Value;

        // If cron is disabled via configuration, it's expected to not be running
        if (!cronConfig.Enabled)
            return Task.FromResult(HealthCheckResult.Healthy("Cron service is disabled via configuration"));

        if (!cronService.IsRunning)
            return Task.FromResult(HealthCheckResult.Unhealthy("Cron tick loop is not running"));

        var jobs = cronService.GetJobs();
        var degradedJobs = jobs
            .Where(j => j.ConsecutiveFailures >= ConsecutiveFailureThreshold)
            .Select(j => $"{j.Name} ({j.ConsecutiveFailures} consecutive failures)")
            .ToList();

        if (degradedJobs.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Jobs with consecutive failures: {string.Join(", ", degradedJobs)}",
                data: new Dictionary<string, object>
                {
                    ["degradedJobs"] = degradedJobs,
                    ["totalJobs"] = jobs.Count
                }));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "Cron service is running",
            data: new Dictionary<string, object> { ["registeredJobs"] = jobs.Count }));
    }
}
