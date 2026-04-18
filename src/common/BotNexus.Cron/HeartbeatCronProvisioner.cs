using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron;

/// <summary>
/// Syncs heartbeat cron jobs from agent configurations on startup.
/// For each agent with heartbeat.enabled = true, ensures a system cron job exists.
/// </summary>
public sealed class HeartbeatCronProvisioner : IHostedService
{
    private const string DefaultHeartbeatPrompt =
        "Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK.";

    private readonly IAgentRegistry _registry;
    private readonly ICronStore _cronStore;
    private readonly ILogger<HeartbeatCronProvisioner> _logger;

    public HeartbeatCronProvisioner(
        IAgentRegistry registry,
        ICronStore cronStore,
        ILogger<HeartbeatCronProvisioner> logger)
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
            var heartbeat = descriptor.Heartbeat;
            var jobId = $"heartbeat:{descriptor.AgentId}";

            if (heartbeat is not { Enabled: true })
            {
                var existing = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (existing is { System: true })
                {
                    await _cronStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Removed heartbeat cron job for agent '{AgentId}'.", descriptor.AgentId);
                }

                continue;
            }

            var interval = Math.Max(1, heartbeat.IntervalMinutes);
            var cronExpression = interval switch
            {
                60 => "0 * * * *",
                _ when interval % 60 == 0 => $"0 */{interval / 60} * * *",
                _ => $"*/{interval} * * * *"
            };

            var prompt = heartbeat.Prompt ?? DefaultHeartbeatPrompt;
            var existingJob = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);

            if (existingJob is null)
            {
                var job = new CronJob
                {
                    Id = jobId,
                    Name = $"Heartbeat — {descriptor.DisplayName}",
                    Schedule = cronExpression,
                    ActionType = "agent-prompt",
                    AgentId = descriptor.AgentId,
                    Message = prompt,
                    Enabled = true,
                    System = true,
                    CreatedBy = "system:heartbeat",
                    CreatedAt = DateTimeOffset.UtcNow
                };

                await _cronStore.CreateAsync(job, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Provisioned heartbeat cron job for agent '{AgentId}' with schedule '{Schedule}'.",
                    descriptor.AgentId, cronExpression);
            }
            else if (existingJob.Schedule != cronExpression
                     || existingJob.Message != prompt
                     || !existingJob.Enabled
                     || !existingJob.System)
            {
                var updated = existingJob with
                {
                    Schedule = cronExpression,
                    Message = prompt,
                    Enabled = true,
                    System = true
                };

                await _cronStore.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Updated heartbeat cron job for agent '{AgentId}' with schedule '{Schedule}'.",
                    descriptor.AgentId, cronExpression);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
