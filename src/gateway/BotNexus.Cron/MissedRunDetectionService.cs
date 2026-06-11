using BotNexus.Cron.Actions;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron;

/// <summary>
/// Runs once on startup to detect cron jobs that missed their scheduled execution window
/// during gateway downtime. Records missed runs and optionally triggers catch-up execution
/// for jobs configured with <c>catchUp: true</c> in metadata.
/// </summary>
public sealed class MissedRunDetectionService(
    ICronStore cronStore,
    CronScheduler scheduler,
    ILogger<MissedRunDetectionService> logger) : IHostedService
{
    internal const string CatchUpMetadataKey = "catchUp";
    internal const string MissedStatus = "missed";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await cronStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var jobs = await cronStore.ListAsync(ct: cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        foreach (var job in jobs)
        {
            if (!job.Enabled || string.IsNullOrWhiteSpace(job.Schedule))
            {
                continue;
            }

            if (job.LastRunAt is null)
            {
                // Never ran — no baseline to detect missed runs from.
                continue;
            }

            var missedRuns = GetMissedRuns(job, now);

            foreach (var missedTime in missedRuns)
            {
                logger.LogWarning(
                    "Cron job '{JobName}' ({JobId}) missed scheduled run at {MissedTime:u}",
                    job.Name, job.Id, missedTime);

                var run = await cronStore.RecordRunStartAsync(job.Id, cancellationToken).ConfigureAwait(false);
                await cronStore.RecordRunCompleteAsync(run.Id, MissedStatus, ct: cancellationToken).ConfigureAwait(false);
            }

            if (missedRuns.Count > 0 && HasCatchUp(job))
            {
                logger.LogInformation(
                    "Triggering catch-up execution for cron job '{JobName}' ({JobId}) — {Count} missed run(s)",
                    job.Name, job.Id, missedRuns.Count);

                try
                {
                    await scheduler.RunNowAsync(job.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Catch-up execution failed for cron job '{JobName}' ({JobId})", job.Name, job.Id);
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Calculates the scheduled run times that were missed between the job's last run and now.
    /// </summary>
    internal static IReadOnlyList<DateTimeOffset> GetMissedRuns(CronJob job, DateTimeOffset now)
    {
        if (job.LastRunAt is null || string.IsNullOrWhiteSpace(job.Schedule))
        {
            return [];
        }

        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(job.Schedule, CronFormat.Standard);
        }
        catch
        {
            return [];
        }

        var tz = TimeZoneHelper.Resolve(job.TimeZone);
        var missedRuns = new List<DateTimeOffset>();

        // Start scanning from lastRunAt; find all occurrences between then and now.
        var cursor = job.LastRunAt.Value.UtcDateTime;
        var limit = now.UtcDateTime;

        // Cap at 100 missed runs to avoid runaway iteration for very frequent schedules after long downtime.
        const int maxMissed = 100;

        while (missedRuns.Count < maxMissed)
        {
            var next = expression.GetNextOccurrence(cursor, tz);
            if (next is null || next.Value >= limit)
            {
                break;
            }

            missedRuns.Add(new DateTimeOffset(next.Value, TimeSpan.Zero));
            cursor = next.Value;
        }

        return missedRuns;
    }

    private static bool HasCatchUp(CronJob job)
    {
        if (job.Metadata is null)
        {
            return false;
        }

        if (!job.Metadata.TryGetValue(CatchUpMetadataKey, out var value))
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
