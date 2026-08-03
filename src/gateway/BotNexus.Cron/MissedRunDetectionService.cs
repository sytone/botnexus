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

    /// <summary>Status stamped on history rows for occurrences that elapsed during downtime.</summary>
    internal const string MissedStatus = CronRunStatus.Missed;

    /// <summary>
    /// Upper bound on missed occurrences recorded per job per scan. Without it a one-minute
    /// schedule after a multi-day outage would write thousands of rows. When the cap bites the
    /// remaining occurrences are dropped, so the scan logs a diagnostic (#2477).
    /// </summary>
    internal const int MaxMissedRunsPerJob = 100;

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

            if (WasTruncated(job, now))
            {
                logger.LogWarning(
                    "Missed-run scan for cron job '{JobName}' ({JobId}) was truncated at the {Cap}-occurrence cap; " +
                    "occurrences before {OldestRecorded:u} were discarded and will never appear in run history.",
                    job.Name, job.Id, MaxMissedRunsPerJob, missedRuns.Count > 0 ? missedRuns[0] : now);
            }

            var recorded = 0;
            foreach (var missedTime in missedRuns)
            {
                // Idempotent by (jobId, scheduledOccurrenceUtc): the missed path never advances
                // last_run_at (nothing executed), so every restart rescans the same window. Writing
                // through TryRecordMissedRunAsync makes the rescan converge instead of duplicating
                // history on each gateway start (#2477).
                var inserted = await cronStore
                    .TryRecordMissedRunAsync(job.Id, missedTime, cancellationToken)
                    .ConfigureAwait(false);

                if (!inserted)
                {
                    continue;
                }

                recorded++;
                logger.LogWarning(
                    "Cron job '{JobName}' ({JobId}) missed scheduled run at {MissedTime:u}",
                    job.Name, job.Id, missedTime);
            }

            if (missedRuns.Count > 0 && recorded == 0)
            {
                logger.LogDebug(
                    "Missed-run scan for cron job '{JobName}' ({JobId}) found {Count} occurrence(s) already recorded by an earlier scan.",
                    job.Name, job.Id, missedRuns.Count);
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
    /// The single floor from which a missed-run scan may replay occurrences: the later of the
    /// job's last run and the instant its current schedule took effect.
    ///
    /// <para>#2554: <c>LastRunAt</c> alone is a property of the job's <b>previous</b> schedule.
    /// Walking the <b>current</b> cron expression forward from it manufactures occurrences that
    /// never existed under the retired schedule - written to history as missed and, for
    /// <c>catchUp: true</c> jobs, fired immediately.</para>
    ///
    /// <para>A null <see cref="CronJob.ScheduleActivatedAt"/> means "unknown" (a row written
    /// before the column existed, or one whose scheduling inputs were never edited). Unknown
    /// deliberately yields no clamp, so jobs whose schedule never changed behave exactly as
    /// before - suppressing a legitimate missed run is worse than the bug being fixed.</para>
    ///
    /// <para>This is the one predicate shared by <see cref="GetMissedRuns"/> and
    /// <see cref="WasTruncated"/>; neither computes its own floor, so the scan and the truncation
    /// warning cannot drift apart.</para>
    /// </summary>
    internal static DateTime? GetScanFloorUtc(CronJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.LastRunAt is null)
        {
            return null;
        }

        var floor = job.LastRunAt.Value.UtcDateTime;
        var activated = job.ScheduleActivatedAt;
        if (activated is not null && activated.Value.UtcDateTime > floor)
        {
            floor = activated.Value.UtcDateTime;
        }

        return floor;
    }

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

        var tz = CronTimeZoneResolver.Resolve(job.TimeZone);
        var missedRuns = new List<DateTimeOffset>();

        // Scan from the shared floor (#2554): max(lastRunAt, scheduleActivatedAt).
        var floor = GetScanFloorUtc(job);
        if (floor is null)
        {
            return [];
        }

        var cursor = floor.Value;
        var limit = now.UtcDateTime;

        // Cap missed runs to avoid runaway iteration for very frequent schedules after long downtime.
        while (missedRuns.Count < MaxMissedRunsPerJob)
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

    /// <summary>
    /// True when the job has at least one further missed occurrence beyond the cap, meaning the
    /// list returned by <see cref="GetMissedRuns"/> silently dropped history.
    /// </summary>
    internal static bool WasTruncated(CronJob job, DateTimeOffset now)
    {
        var missed = GetMissedRuns(job, now);
        if (missed.Count < MaxMissedRunsPerJob)
        {
            return false;
        }

        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(job.Schedule, CronFormat.Standard);
        }
        catch
        {
            return false;
        }

        var tz = CronTimeZoneResolver.Resolve(job.TimeZone);

        // Continue from the last recorded occurrence, which the shared floor already bounded
        // (#2554) — WasTruncated must never look at a window GetMissedRuns refused to scan.
        var next = expression.GetNextOccurrence(missed[^1].UtcDateTime, tz);
        return next is not null && next.Value < now.UtcDateTime;
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
