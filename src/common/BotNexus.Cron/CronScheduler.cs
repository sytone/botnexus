using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron;

public sealed class CronScheduler(
    ICronStore cronStore,
    IEnumerable<ICronAction> actions,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<CronOptions> optionsMonitor,
    ILogger<CronScheduler> logger) : BackgroundService
{
    private readonly ICronStore _cronStore = cronStore;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IOptionsMonitor<CronOptions> _optionsMonitor = optionsMonitor;
    private readonly ILogger<CronScheduler> _logger = logger;
    private readonly IReadOnlyDictionary<string, ICronAction> _actions = actions
        .GroupBy(action => action.ActionType, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

    public async Task<CronRun> RunNowAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await _cronStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var job = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Cron job '{jobId}' was not found.");

        return await RunActionAsync(job, CronTriggerType.Manual, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _cronStore.InitializeAsync(stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Cron scheduler started. Tick interval: {Interval}s", _optionsMonitor.CurrentValue?.TickIntervalSeconds ?? 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue ?? new CronOptions();
            await SyncConfiguredJobsAsync(options, stoppingToken).ConfigureAwait(false);
            if (options.Enabled)
            {
                try
                {
                    await ProcessTickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cron scheduler tick failed.");
                }
            }

            var delay = TimeSpan.FromSeconds(Math.Max(1, options.TickIntervalSeconds));
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessTickAsync(CancellationToken ct)
    {
        var jobs = await _cronStore.ListAsync(ct: ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        foreach (var job in jobs.Where(j => j.Enabled))
        {
            if (!TryGetSchedule(job, out var expression))
                continue;

            var tz = ResolveTimeZone(job);
            var computedNext = expression.GetNextOccurrence(now, tz);

            if (job.NextRunAt is null)
            {
                var initialized = job with
                {
                    NextRunAt = computedNext
                };
                await _cronStore.UpdateAsync(initialized, ct).ConfigureAwait(false);
                continue;
            }

            // Detect stale NextRunAt: if the schedule was changed to fire sooner
            // than the stored value, correct it so the job isn't stuck waiting on
            // a NextRunAt that no longer matches the current schedule.
            if (computedNext is not null && computedNext < job.NextRunAt)
            {
                var corrected = job with { NextRunAt = computedNext };
                await _cronStore.UpdateAsync(corrected, ct).ConfigureAwait(false);
                if (computedNext > now)
                    continue;
            }

            if (job.NextRunAt > now)
                continue;

            await RunActionAsync(job, CronTriggerType.Scheduled, now, ct).ConfigureAwait(false);

            var latest = await _cronStore.GetAsync(job.Id, ct).ConfigureAwait(false) ?? job;
            var updated = latest with
            {
                NextRunAt = expression.GetNextOccurrence(now, tz)
            };
            await _cronStore.UpdateAsync(updated, ct).ConfigureAwait(false);
        }
    }

    private async Task<CronRun> RunActionAsync(CronJob job, CronTriggerType triggerType, DateTimeOffset triggeredAt, CancellationToken ct)
    {
        var run = await _cronStore.RecordRunStartAsync(job.Id, ct).ConfigureAwait(false);
        var action = ResolveAction(job.ActionType);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = new CronExecutionContext
            {
                Job = job,
                RunId = run.Id,
                TriggeredAt = triggeredAt,
                TriggerType = triggerType,
                Services = scope.ServiceProvider
            };

            await action.ExecuteAsync(context, ct).ConfigureAwait(false);
            _logger.LogInformation("Cron job executed: {JobName} ({JobId}) action={ActionType} trigger={TriggerType}",
                job.Name, job.Id, job.ActionType, triggerType);
            await _cronStore.RecordRunCompleteAsync(run.Id, "ok", sessionId: context.SessionId, ct: ct).ConfigureAwait(false);

            // Re-read the job before updating run status to avoid clobbering
            // concurrent changes (schedule updates, NextRunAt corrections, etc.)
            // that occurred while the action was executing.
            var latest = await _cronStore.GetAsync(job.Id, ct).ConfigureAwait(false) ?? job;
            await _cronStore.UpdateAsync(latest with
            {
                LastRunAt = triggeredAt,
                LastRunStatus = "ok",
                LastRunError = null
            }, ct).ConfigureAwait(false);
            return run with { Status = "ok", CompletedAt = DateTimeOffset.UtcNow, SessionId = context.SessionId };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Cron job execution failed. JobId: {JobId}, ActionType: {ActionType}", job.Id, job.ActionType);
            await _cronStore.RecordRunCompleteAsync(run.Id, "error", ex.Message, ct: ct).ConfigureAwait(false);

            var latest = await _cronStore.GetAsync(job.Id, ct).ConfigureAwait(false) ?? job;
            await _cronStore.UpdateAsync(latest with
            {
                LastRunAt = triggeredAt,
                LastRunStatus = "error",
                LastRunError = ex.ToString()
            }, ct).ConfigureAwait(false);
            return run with { Status = "error", CompletedAt = DateTimeOffset.UtcNow, Error = ex.Message };
        }
    }

    private ICronAction ResolveAction(string actionType)
    {
        if (_actions.TryGetValue(actionType, out var action))
            return action;

        throw new InvalidOperationException($"No cron action registered for type '{actionType}'.");
    }

    private bool TryGetSchedule(CronJob job, out CronExpression expression)
    {
        try
        {
            expression = CronExpression.Parse(job.Schedule, CronFormat.Standard);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid cron expression for job {JobId}: {Schedule}", job.Id, job.Schedule);
            expression = default!;
            return false;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(CronJob job)
    {
        if (string.IsNullOrWhiteSpace(job.TimeZone))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(job.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private async Task SyncConfiguredJobsAsync(CronOptions options, CancellationToken ct)
    {
        if (options.Jobs is null || options.Jobs.Count == 0)
            return;

        foreach (var (jobId, configuredJob) in options.Jobs)
        {
            if (string.IsNullOrWhiteSpace(jobId) ||
                string.IsNullOrWhiteSpace(configuredJob.Schedule) ||
                string.IsNullOrWhiteSpace(configuredJob.ActionType))
            {
                continue;
            }

            var existing = await _cronStore.GetAsync(jobId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                var seeded = new CronJob
                {
                    Id = jobId,
                    Name = configuredJob.Name ?? jobId,
                    Schedule = configuredJob.Schedule,
                    ActionType = configuredJob.ActionType,
                    AgentId = configuredJob.AgentId,
                    Message = configuredJob.Message,
                    WebhookUrl = configuredJob.WebhookUrl,
                    ShellCommand = configuredJob.ShellCommand,
                    Enabled = configuredJob.Enabled,
                    System = configuredJob.System,
                    TimeZone = configuredJob.TimeZone,
                    CreatedBy = configuredJob.CreatedBy,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Metadata = configuredJob.Metadata
                };
                await _cronStore.CreateAsync(seeded, ct).ConfigureAwait(false);
                continue;
            }

            var merged = existing with
            {
                Name = configuredJob.Name ?? existing.Name,
                Schedule = configuredJob.Schedule,
                ActionType = configuredJob.ActionType,
                AgentId = configuredJob.AgentId,
                Message = configuredJob.Message,
                WebhookUrl = configuredJob.WebhookUrl,
                ShellCommand = configuredJob.ShellCommand,
                Enabled = configuredJob.Enabled,
                System = configuredJob.System,
                TimeZone = configuredJob.TimeZone ?? existing.TimeZone,
                CreatedBy = configuredJob.CreatedBy ?? existing.CreatedBy,
                Metadata = configuredJob.Metadata ?? existing.Metadata
            };

            await _cronStore.UpdateAsync(merged, ct).ConfigureAwait(false);
        }
    }
}
