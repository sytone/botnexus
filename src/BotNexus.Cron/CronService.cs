using BotNexus.Core.Abstractions;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron;

/// <summary>
/// Cron job scheduling service that runs as an IHostedService.
/// Uses Cronos for expression parsing and scheduling.
/// </summary>
public sealed class CronService : BackgroundService, ICronService
{
    private readonly Dictionary<string, CronJob> _jobs = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<CronService> _logger;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    public CronService(ILogger<CronService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Schedule(string name, string cronExpression, Func<CancellationToken, Task> action, TimeZoneInfo? timeZone = null)
    {
        var expression = CronExpression.Parse(cronExpression, CronFormat.Standard);
        var tz = timeZone ?? TimeZoneInfo.Utc;
        var job = new CronJob(name, expression, action, tz);
        job.NextOccurrence = expression.GetNextOccurrence(DateTimeOffset.UtcNow, tz);

        _lock.Wait();
        try
        {
            _jobs[name] = job;
            _logger.LogInformation("Cron job '{Name}' scheduled with expression '{Expression}'", name, cronExpression);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc/>
    public void Remove(string name)
    {
        _lock.Wait();
        try
        {
            _jobs.Remove(name);
            _logger.LogInformation("Cron job '{Name}' removed", name);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetScheduledJobs()
    {
        _lock.Wait();
        try { return [.. _jobs.Keys]; }
        finally { _lock.Release(); }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cron service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        List<CronJob> dueJobs;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            dueJobs = _jobs.Values
                .Where(j => j.NextOccurrence.HasValue && j.NextOccurrence.Value <= now)
                .ToList();

            foreach (var job in dueJobs)
                job.NextOccurrence = job.Expression.GetNextOccurrence(now, job.TimeZone);
        }
        finally { _lock.Release(); }

        foreach (var job in dueJobs)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Executing cron job '{Name}'", job.Name);
                    await job.Action(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cron job '{Name}' failed", job.Name);
                }
            }, cancellationToken);
        }
    }
}
