using System.Collections.Concurrent;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Core.Observability;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron;

/// <summary>
/// Central cron scheduler that evaluates registered jobs on a fixed tick interval.
/// </summary>
public sealed class CronService : BackgroundService, ICronService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CronJobEntry> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<CronJobExecution>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CronService> _logger;
    private readonly IServiceProvider _services;
    private readonly IActivityStream _activityStream;
    private readonly IBotNexusMetrics _metrics;
    private readonly IOptionsMonitor<BotNexusConfig> _configMonitor;
    private readonly CronJobFactory? _cronJobFactory;
    private TimeSpan _tickInterval;
    private volatile int _executionHistorySize;
    private volatile bool _enabled;
    private volatile bool _isRunning;

    public CronService(
        ILogger<CronService> logger,
        IServiceProvider services,
        IActivityStream activityStream,
        IBotNexusMetrics metrics,
        IOptionsMonitor<BotNexusConfig> optionsMonitor,
        CronJobFactory? cronJobFactory = null)
    {
        _logger = logger;
        _services = services;
        _activityStream = activityStream;
        _metrics = metrics;
        _configMonitor = optionsMonitor;
        _cronJobFactory = cronJobFactory;

        ApplyCronSettings(_configMonitor.CurrentValue.Cron);
    }

    /// <inheritdoc />
    public bool IsRunning => _isRunning;

    /// <inheritdoc />
    public void Register(ICronJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var expression = ParseExpression(job.Schedule);
        var now = DateTimeOffset.UtcNow;
        var entry = new CronJobEntry(job, expression)
        {
            NextOccurrence = expression.GetNextOccurrence(now, job.TimeZone ?? TimeZoneInfo.Utc)
        };

        lock (_sync)
        {
            _jobs[job.Name] = entry;
            if (!_history.ContainsKey(job.Name))
                _history[job.Name] = new Queue<CronJobExecution>(_executionHistorySize);
        }

        _logger.LogInformation(
            "Registered cron job '{JobName}' ({Type}) schedule '{Schedule}'",
            job.Name,
            job.Type,
            job.Schedule);
    }

    /// <inheritdoc />
    public void Remove(string jobName)
    {
        lock (_sync)
        {
            _jobs.Remove(jobName);
            _history.Remove(jobName);
        }

        _logger.LogInformation("Removed cron job '{JobName}'", jobName);
    }

    /// <inheritdoc />
    public IReadOnlyList<CronJobStatus> GetJobs()
    {
        lock (_sync)
        {
            return _jobs.Values
                .Select(entry => new CronJobStatus(
                    entry.Job.Name,
                    entry.Job.Type,
                    entry.Job.Schedule,
                    entry.Job.Enabled,
                    entry.LastRunStartedAt,
                    entry.NextOccurrence,
                    entry.LastResult?.Success,
                    entry.LastResult?.Duration,
                    entry.ConsecutiveFailures))
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CronJobExecution> GetHistory(string jobName, int limit = 10)
    {
        if (limit <= 0)
            return [];

        lock (_sync)
        {
            if (!_history.TryGetValue(jobName, out var executions))
                return [];

            return executions
                .Reverse()
                .Take(limit)
                .ToList();
        }
    }

    /// <inheritdoc />
    public Task TriggerAsync(string jobName, CancellationToken cancellationToken = default)
    {
        CronJobEntry entry;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobName, out entry!))
                throw new InvalidOperationException($"Cron job '{jobName}' not found.");

            if (entry.IsRunning)
                return Task.CompletedTask;

            entry.IsRunning = true;
        }

        QueueExecution(entry, DateTimeOffset.UtcNow, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetEnabled(string jobName, bool enabled)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobName, out var entry))
                return;

            entry.Job.Enabled = enabled;
            if (enabled)
            {
                entry.NextOccurrence = entry.Expression.GetNextOccurrence(
                    DateTimeOffset.UtcNow,
                    entry.Job.TimeZone ?? TimeZoneInfo.Utc);
            }
        }

        _logger.LogInformation("Cron job '{JobName}' enabled={Enabled}", jobName, enabled);
    }

    /// <inheritdoc />
    public Task ReloadFromConfigAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cronConfig = _configMonitor.CurrentValue.Cron ?? new CronConfig();
        ApplyCronSettings(cronConfig);

        if (_cronJobFactory is null)
        {
            _logger.LogWarning("Cron reload requested but no CronJobFactory is available.");
            return Task.CompletedTask;
        }

        var desiredJobs = _cronJobFactory.CreateJobs();
        Dictionary<string, ICronJob> currentJobs;
        lock (_sync)
        {
            currentJobs = _jobs.Values.ToDictionary(entry => entry.Job.Name, entry => entry.Job, StringComparer.OrdinalIgnoreCase);
        }

        var removed = currentJobs.Keys
            .Except(desiredJobs.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var jobName in removed)
            Remove(jobName);

        var added = 0;
        var updated = 0;
        foreach (var (jobName, desiredJob) in desiredJobs)
        {
            if (!currentJobs.TryGetValue(jobName, out var existingJob))
            {
                Register(desiredJob);
                added++;
                continue;
            }

            if (!IsEquivalent(existingJob, desiredJob))
            {
                Remove(jobName);
                Register(desiredJob);
                updated++;
            }
            else
            {
                SetEnabled(jobName, desiredJob.Enabled);
            }
        }

        _logger.LogInformation(
            "Cron config reloaded. Added={Added}, Updated={Updated}, Removed={Removed}, Enabled={Enabled}, TickIntervalSeconds={TickIntervalSeconds}",
            added,
            updated,
            removed.Count,
            _enabled,
            _tickInterval.TotalSeconds);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _isRunning = true;
        _logger.LogInformation("Cron service started with tick interval {TickIntervalSeconds}s", _tickInterval.TotalSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_tickInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await TickAsync(stoppingToken).ConfigureAwait(false);
            }

            await AwaitRunningTasksAsync().ConfigureAwait(false);
        }
        finally
        {
            _isRunning = false;
        }
    }

    private Task TickAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
            return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow;
        List<(CronJobEntry Entry, DateTimeOffset ScheduledTime)> dueJobs = [];

        lock (_sync)
        {
            foreach (var entry in _jobs.Values)
            {
                if (!entry.Job.Enabled)
                {
                    if (entry.NextOccurrence.HasValue && entry.NextOccurrence.Value <= now)
                        _metrics.IncrementCronJobsSkipped(entry.Job.Name, "disabled");
                    continue;
                }

                if (entry.IsRunning)
                {
                    if (entry.NextOccurrence.HasValue && entry.NextOccurrence.Value <= now)
                        _metrics.IncrementCronJobsSkipped(entry.Job.Name, "overlapping");
                    continue;
                }

                if (!entry.NextOccurrence.HasValue || entry.NextOccurrence.Value > now)
                    continue;

                var scheduledTime = entry.NextOccurrence.Value;
                entry.IsRunning = true;
                entry.NextOccurrence = entry.Expression.GetNextOccurrence(now, entry.Job.TimeZone ?? TimeZoneInfo.Utc);
                dueJobs.Add((entry, scheduledTime));
            }
        }

        foreach (var due in dueJobs)
            QueueExecution(due.Entry, due.ScheduledTime, cancellationToken);

        return Task.CompletedTask;
    }

    private void QueueExecution(CronJobEntry entry, DateTimeOffset scheduledTime, CancellationToken cancellationToken)
    {
        var task = ExecuteJobAsync(entry, scheduledTime, cancellationToken);
        _runningTasks[entry.Job.Name] = task;
    }

    private async Task ExecuteJobAsync(CronJobEntry entry, DateTimeOffset scheduledTime, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid().ToString("N");
        var context = new CronJobContext
        {
            JobName = entry.Job.Name,
            CorrelationId = correlationId,
            ScheduledTime = scheduledTime,
            ActualTime = startedAt,
            Services = _services
        };

        _metrics.IncrementCronJobsExecuted(entry.Job.Name);

        CronJobResult result = new(
            Success: false,
            Error: "Job did not complete.",
            Duration: TimeSpan.Zero);
        var completedAt = startedAt;

        try
        {
            await PublishActivitySafeAsync(
                ActivityEventType.AgentProcessing,
                "cron.job.started",
                entry.Job,
                context,
                cancellationToken).ConfigureAwait(false);

            try
            {
                result = await entry.Job.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new CronJobResult(
                    Success: false,
                    Error: ex.Message,
                    Duration: DateTimeOffset.UtcNow - startedAt);
                _logger.LogError(ex, "Cron job '{JobName}' failed unexpectedly", entry.Job.Name);
            }

            completedAt = DateTimeOffset.UtcNow;
            var duration = result.Duration == default ? completedAt - startedAt : result.Duration;
            result = result with { Duration = duration };

            _metrics.RecordCronJobDuration(entry.Job.Name, duration.TotalMilliseconds);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Cron job '{JobName}' completed in {DurationMs}ms",
                    entry.Job.Name,
                    duration.TotalMilliseconds);

                await PublishActivitySafeAsync(
                    ActivityEventType.AgentCompleted,
                    "cron.job.completed",
                    entry.Job,
                    context,
                    cancellationToken,
                    result.Output,
                    new Dictionary<string, object>
                    {
                        ["duration_ms"] = duration.TotalMilliseconds,
                        ["success"] = true
                    }).ConfigureAwait(false);
            }
            else
            {
                _metrics.IncrementCronJobsFailed(entry.Job.Name);

                _logger.LogWarning(
                    "Cron job '{JobName}' failed in {DurationMs}ms: {Error}",
                    entry.Job.Name,
                    duration.TotalMilliseconds,
                    result.Error);

                await PublishActivitySafeAsync(
                    ActivityEventType.Error,
                    "cron.job.failed",
                    entry.Job,
                    context,
                    cancellationToken,
                    result.Error,
                    new Dictionary<string, object>
                    {
                        ["duration_ms"] = duration.TotalMilliseconds,
                        ["success"] = false,
                        ["error"] = result.Error ?? string.Empty
                    }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            completedAt = DateTimeOffset.UtcNow;
            result = result with { Success = false, Error = ex.Message, Duration = completedAt - startedAt };
            _metrics.IncrementCronJobsFailed(entry.Job.Name);
            _logger.LogError(ex, "Cron job '{JobName}' execution pipeline failed", entry.Job.Name);
        }
        finally
        {
            var execution = new CronJobExecution(
                entry.Job.Name,
                correlationId,
                startedAt,
                completedAt,
                result.Success,
                result.Output,
                result.Error);

            lock (_sync)
            {
                entry.LastRunStartedAt = startedAt;
                entry.LastResult = result;
                entry.ConsecutiveFailures = result.Success ? 0 : entry.ConsecutiveFailures + 1;
                entry.IsRunning = false;

                if (!_history.TryGetValue(entry.Job.Name, out var history))
                {
                    history = new Queue<CronJobExecution>(_executionHistorySize);
                    _history[entry.Job.Name] = history;
                }

                history.Enqueue(execution);
                while (history.Count > _executionHistorySize)
                    history.Dequeue();
            }

            _runningTasks.TryRemove(entry.Job.Name, out _);
        }
    }

    private async Task PublishActivityAsync(
        ActivityEventType eventType,
        string eventName,
        ICronJob job,
        CronJobContext context,
        CancellationToken cancellationToken,
        string? details = null,
        Dictionary<string, object>? extraMetadata = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["event"] = eventName,
            ["source"] = "cron",
            ["job_name"] = job.Name,
            ["job_type"] = job.Type.ToString(),
            ["correlation_id"] = context.CorrelationId,
            ["scheduled_time"] = context.ScheduledTime.ToString("O"),
            ["actual_time"] = context.ActualTime.ToString("O")
        };

        if (extraMetadata is not null)
        {
            foreach (var kvp in extraMetadata)
                metadata[kvp.Key] = kvp.Value;
        }

        await _activityStream.PublishAsync(new ActivityEvent(
            eventType,
            "cron",
            $"cron:{job.Name}",
            job.Name,
            "cron-service",
            details ?? eventName,
            DateTimeOffset.UtcNow,
            metadata), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishActivitySafeAsync(
        ActivityEventType eventType,
        string eventName,
        ICronJob job,
        CronJobContext context,
        CancellationToken cancellationToken,
        string? details = null,
        Dictionary<string, object>? extraMetadata = null)
    {
        try
        {
            await PublishActivityAsync(eventType, eventName, job, context, cancellationToken, details, extraMetadata).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish cron activity event {EventName} for {JobName}", eventName, job.Name);
        }
    }

    private async Task AwaitRunningTasksAsync()
    {
        var tasks = _runningTasks.Values.ToArray();
        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Individual failures are already handled and recorded per job execution.
        }
    }

    private static CronExpression ParseExpression(string schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            throw new ArgumentException("Cron schedule cannot be empty.", nameof(schedule));

        try
        {
            return CronExpression.Parse(schedule, CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            return CronExpression.Parse(schedule, CronFormat.IncludeSeconds);
        }
    }

    private sealed class CronJobEntry(ICronJob job, CronExpression expression)
    {
        public ICronJob Job { get; } = job;
        public CronExpression Expression { get; } = expression;
        public DateTimeOffset? NextOccurrence { get; set; }
        public DateTimeOffset? LastRunStartedAt { get; set; }
        public CronJobResult? LastResult { get; set; }
        public int ConsecutiveFailures { get; set; }
        public bool IsRunning { get; set; }
    }

    private void ApplyCronSettings(CronConfig? cronConfig)
    {
        var cron = cronConfig ?? new CronConfig();
        _enabled = cron.Enabled;
        _tickInterval = TimeSpan.FromSeconds(cron.TickIntervalSeconds > 0 ? cron.TickIntervalSeconds : 10);
        _executionHistorySize = cron.ExecutionHistorySize > 0 ? cron.ExecutionHistorySize : 100;
    }

    private static bool IsEquivalent(ICronJob existingJob, ICronJob desiredJob)
    {
        var existingZone = existingJob.TimeZone?.Id ?? string.Empty;
        var desiredZone = desiredJob.TimeZone?.Id ?? string.Empty;
        return existingJob.GetType() == desiredJob.GetType()
            && existingJob.Type == desiredJob.Type
            && string.Equals(existingJob.Schedule, desiredJob.Schedule, StringComparison.Ordinal)
            && string.Equals(existingZone, desiredZone, StringComparison.Ordinal)
            && existingJob.Enabled == desiredJob.Enabled;
    }

}
