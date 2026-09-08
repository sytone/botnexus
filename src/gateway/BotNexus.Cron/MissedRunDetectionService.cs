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
    ILogger<MissedRunDetectionService> logger,
    TimeProvider? timeProvider = null) : IHostedService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
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
        var now = _timeProvider.GetUtcNow();

        foreach (var job in jobs)
        {
            if (!job.Enabled || string.IsNullOrWhiteSpace(job.Schedule))
            {
                continue;
            }

            // #3546: a job past its expiry is dropped from the missed-run scan exactly as it is
            // dropped from the due scan and the fire path (#2634). Its post-expiry occurrences are
            // by-design non-events, not missed runs: recording them corrupts run history and cost
            // rollups, and lets an expired job burn the 100-occurrence cap so the truncation
            // warning fires for a window that should never have been scanned. The predicate is the
            // shared one in CronJobExpiry - the scanner deliberately owns no ExpiresAt comparison
            // of its own.
            //
            // This is the whole-job early-out. The partial case - a job that expired PARTWAY
            // through the window - is handled by the ceiling clamp inside GetMissedRuns, which
            // keeps the occurrences that fell strictly before ExpiresAt.
            if (CronJobExpiry.IsExpired(job, now))
            {
                logger.LogDebug(
                    "Cron job '{JobName}' ({JobId}) is past its expiry ({ExpiresAt:o}); skipping the missed-run scan.",
                    job.Name, job.Id, job.ExpiresAt);
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

        var tz = CronTimeZoneResolver.Resolve(job.TimeZone, jobId: job.Id);

        // Scan from the shared floor (#2554): max(lastRunAt, scheduleActivatedAt).
        var floor = GetScanFloorUtc(job);
        if (floor is null)
        {
            return [];
        }

        // ...and up to the shared ceiling (#3546): min(now, expiresAt). Expiry clamps the window's
        // UPPER bound, the exact mirror of the #2554 lower-bound clamp, so a job that expired
        // partway through the window keeps the occurrences that really were missed and drops only
        // the ones at or after the expiry instant. ExpiresAt = null yields `now` unchanged, which
        // is what keeps non-expiring behaviour byte-identical.
        var ceiling = CronJobExpiry.GetScanCeilingUtc(job, now);
        if (ceiling <= floor.Value)
        {
            return [];
        }

        // #2810: this walk advances a cursor through HISTORY, so it is the one next-run computation
        // that necessarily crosses past DST transitions. It is therefore defined in
        // CronExpressionExtensions alongside the forward computation - the two must agree instant for
        // instant, and a local loop here could drift from the forward path without anything noticing.
        // The cap still bounds runaway iteration for frequent schedules after long downtime.
        return expression
            .RunsBetweenUtc(floor.Value, ceiling, MaxMissedRunsPerJob, tz)
            .Select(occurrence => new DateTimeOffset(occurrence, TimeSpan.Zero))
            .ToList();
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

        var tz = CronTimeZoneResolver.Resolve(job.TimeZone, jobId: job.Id);

        // Continue from the last recorded occurrence, which the shared floor already bounded
        // (#2554) — WasTruncated must never look at a window GetMissedRuns refused to scan. The
        // same applies at the top end (#3546): compare against the expiry-clamped ceiling, so an
        // expired job cannot report truncation for occurrences the scan correctly declined to walk.
        var next = expression.NextRunUtc(missed[^1].UtcDateTime, tz);
        return next is not null && next.Value < CronJobExpiry.GetScanCeilingUtc(job, now);
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
