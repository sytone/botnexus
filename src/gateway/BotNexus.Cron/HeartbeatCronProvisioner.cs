using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron;

/// <summary>
/// Syncs heartbeat cron jobs from agent configurations on startup.
/// For each agent with heartbeat.enabled = true, ensures a system cron job exists.
/// Also implements <see cref="IHeartbeatProvisioner"/> for runtime re-provisioning
/// when agents are registered or updated via the API.
/// </summary>
public sealed class HeartbeatCronProvisioner : IHostedService, IHeartbeatProvisioner
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
            await ProvisionAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task ProvisionAsync(AgentDescriptor descriptor, CancellationToken cancellationToken)
    {
        var heartbeat = descriptor.Heartbeat;
        var jobId = JobId.From($"heartbeat:{descriptor.AgentId.Value}");

        if (heartbeat is not { Enabled: true })
        {
            var existing = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (existing is { System: true })
            {
                await _cronStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Removed heartbeat cron job for agent '{AgentId}'.", descriptor.AgentId);
            }

            return;
        }

        // Validate active hours config before proceeding.
        if (heartbeat.ActiveHours is { } activeHours)
        {
            var validationError = activeHours.Validate();
            if (validationError is not null)
            {
                _logger.LogWarning(
                    "Skipping heartbeat provisioning for agent '{AgentId}': {Error}",
                    descriptor.AgentId, validationError);
                return;
            }
        }

        var cronExpression = BuildCronExpression(heartbeat);
        var timezone = heartbeat.ActiveHours?.Timezone;
        var prompt = heartbeat.Prompt ?? DefaultHeartbeatPrompt;
        var existingJob = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (existingJob is null)
        {
            var job = new CronJob
            {
                Id = jobId,
                Name = $"Heartbeat \u2014 {descriptor.DisplayName}",
                Schedule = cronExpression,
                ActionType = "heartbeat",
                AgentId = descriptor.AgentId,
                Message = prompt,
                Enabled = true,
                System = true,
                TimeZone = timezone,
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
                 || existingJob.TimeZone != timezone
                 || !existingJob.Enabled
                 || !existingJob.System)
        {
            var updated = existingJob with
            {
                Schedule = cronExpression,
                Message = prompt,
                Enabled = true,
                System = true,
                TimeZone = timezone
            };

            // #2133: heartbeat provisioning is a definition write; the scheduler owns
            // NextRunAt/LastRun* and CAS owns the conversation pin, so use the narrow write.
            await _cronStore.UpdateDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Updated heartbeat cron job for agent '{AgentId}' with schedule '{Schedule}'.",
                descriptor.AgentId, cronExpression);
        }
    }

    /// <summary>
    /// Builds the cron expression for a heartbeat job.
    /// When <see cref="HeartbeatAgentConfig.ActiveHours"/> is set, the hour range is baked
    /// directly into the expression so the scheduler only fires within the window.
    /// </summary>
    public static string BuildCronExpression(HeartbeatAgentConfig heartbeat)
    {
        var interval = Math.Max(1, heartbeat.IntervalMinutes);

        if (heartbeat.ActiveHours is { } active)
        {
            var start = ActiveHoursConfig.ParseTime(active.Start)!.Value;
            var end = ActiveHoursConfig.ParseTime(active.End)!.Value;

            // End hour: if end minute is 00, the last full hour slot is end.Hour - 1.
            // e.g. window 08:00-23:00 fires up to and including 22:xx -> hour range 8-22.
            // e.g. window 08:00-23:30 fires up to and including 23:xx -> hour range 8-23.
            var lastHour = end.Minute == 0 ? end.Hour - 1 : end.Hour;

            var minutePart = interval switch
            {
                60 => "0",
                _ when interval % 60 == 0 => $"0",   // multi-hour intervals still fire at :00
                _ => $"*/{interval}"
            };

            var hourPart = start.Hour == lastHour
                ? start.Hour.ToString()
                : $"{start.Hour}-{lastHour}";

            // Multi-hour intervals: e.g. every 2 hours starting at 8 -> "0 8-22/2 * * *"
            if (interval >= 60 && interval % 60 == 0)
            {
                var hourStep = interval / 60;
                hourPart = hourStep == 1
                    ? $"{start.Hour}-{lastHour}"
                    : $"{start.Hour}-{lastHour}/{hourStep}";
                return $"0 {hourPart} * * *";
            }

            return $"{minutePart} {hourPart} * * *";
        }

        // No active hours - plain interval expression.
        return interval switch
        {
            60 => "0 * * * *",
            _ when interval % 60 == 0 => $"0 */{interval / 60} * * *",
            _ => $"*/{interval} * * * *"
        };
    }
}
