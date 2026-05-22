using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron;

/// <summary>
/// Syncs memory dreaming cron jobs from agent configurations on startup.
/// For each agent with memoryDreaming.enabled = true, ensures a system cron job exists
/// that runs on the configured schedule and prompts the agent to consolidate daily notes
/// into MEMORY.md.
/// </summary>
public sealed class MemoryDreamingProvisioner : IHostedService
{
    internal const string DefaultDreamingPrompt =
        "Memory consolidation run. " +
        "Read the last 7 days of daily memory files (memory/YYYY-MM-DD.md). " +
        "Identify important decisions, recurring patterns, and items that should be persisted long-term. " +
        "Update MEMORY.md with consolidated insights, keeping it concise and actionable. " +
        "Archive or summarize processed daily notes where appropriate.";

    private readonly IAgentRegistry _registry;
    private readonly ICronStore _cronStore;
    private readonly ILogger<MemoryDreamingProvisioner> _logger;

    public MemoryDreamingProvisioner(
        IAgentRegistry registry,
        ICronStore cronStore,
        ILogger<MemoryDreamingProvisioner> logger)
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
    /// Provisions or removes the memory dreaming cron job for an agent descriptor.
    /// Called on startup and when agents are registered or updated at runtime.
    /// </summary>
    public async Task ProvisionAsync(AgentDescriptor descriptor, CancellationToken cancellationToken)
    {
        var dreaming = descriptor.MemoryDreaming;
        var jobId = $"memory-dreaming:{descriptor.AgentId}";

        if (dreaming is not { Enabled: true })
        {
            var existing = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (existing is { System: true })
            {
                await _cronStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Removed memory dreaming cron job for agent '{AgentId}'.", descriptor.AgentId);
            }

            return;
        }

        var schedule = !string.IsNullOrWhiteSpace(dreaming.Schedule)
            ? dreaming.Schedule
            : "0 3 * * *";

        var timezone = dreaming.Timezone;
        var prompt = dreaming.Prompt ?? BuildDefaultPrompt(dreaming.LookbackDays);
        var existingJob = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (existingJob is null)
        {
            var job = new CronJob
            {
                Id = jobId,
                Name = $"Memory Dreaming \u2014 {descriptor.DisplayName}",
                Schedule = schedule,
                ActionType = "agent-prompt",
                AgentId = descriptor.AgentId,
                Message = prompt,
                Enabled = true,
                System = true,
                TimeZone = timezone,
                CreatedBy = "system:memory-dreaming",
                CreatedAt = DateTimeOffset.UtcNow
            };

            await _cronStore.CreateAsync(job, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Provisioned memory dreaming cron job for agent '{AgentId}' with schedule '{Schedule}'.",
                descriptor.AgentId, schedule);
        }
        else if (existingJob.Schedule != schedule
                 || existingJob.Message != prompt
                 || existingJob.TimeZone != timezone
                 || !existingJob.Enabled
                 || !existingJob.System)
        {
            var updated = existingJob with
            {
                Schedule = schedule,
                Message = prompt,
                Enabled = true,
                System = true,
                TimeZone = timezone
            };

            await _cronStore.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Updated memory dreaming cron job for agent '{AgentId}' with schedule '{Schedule}'.",
                descriptor.AgentId, schedule);
        }
    }

    public static string BuildDefaultPrompt(int lookbackDays)
    {
        var days = Math.Max(1, lookbackDays);
        return
            "Memory consolidation run. " +
            $"Read the last {days} day{(days == 1 ? "" : "s")} of daily memory files (memory/YYYY-MM-DD.md). " +
            "Identify important decisions, recurring patterns, and items that should be persisted long-term. " +
            "Update MEMORY.md with consolidated insights, keeping it concise and actionable. " +
            "Archive or summarize processed daily notes where appropriate.";
    }
}
