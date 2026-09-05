namespace BotNexus.Cron;

/// <summary>
/// The single expiry predicate for cron jobs (#2634), extracted from <see cref="CronScheduler"/>
/// by #3546 so that every consumer of the concept shares one comparison.
///
/// <para>Before #3546 the predicate lived as a private method on <see cref="CronScheduler"/> and was
/// therefore unreachable from <see cref="MissedRunDetectionService"/>. The scanner consequently
/// ignored expiry entirely and replayed the whole post-expiry occurrence window as missed runs on
/// every gateway start. Extracting it here is deliberate: the alternative - a second inline
/// <c>ExpiresAt</c> comparison in the scanner - is exactly the duplicated-implementation shape that
/// let the two paths disagree in the first place.</para>
///
/// <para>The scheduler's own two suppression sites (the due-scan early-out and the fire-time gate)
/// now route through <see cref="IsExpired"/> via <c>CronScheduler.IsExpired</c>, which is a thin
/// adapter binding this predicate to the scheduler's <see cref="TimeProvider"/>. There is no second
/// copy of the comparison anywhere in <c>BotNexus.Cron</c>.</para>
/// </summary>
internal static class CronJobExpiry
{
    /// <summary>
    /// Whether <paramref name="job"/> is past its <see cref="CronJob.ExpiresAt"/> instant at
    /// <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> expiry is <b>never</b> expired: NULL means "no expiry", so a job that does not
    /// carry the field behaves exactly as it did before the field existed. The comparison is
    /// inclusive (<c>&gt;=</c>) so the expiry instant itself is already past - "stops executing
    /// after that instant" must not leave a one-tick window where a fire still lands.
    /// </remarks>
    internal static bool IsExpired(CronJob job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job.ExpiresAt is { } expiresAt && now >= expiresAt;
    }

    /// <summary>
    /// The upper bound a historical scan may walk to for <paramref name="job"/>: the earlier of
    /// <paramref name="now"/> and the job's expiry instant (#3546).
    ///
    /// <para>Expiry <b>clamps the window</b> rather than discarding the job wholesale - the exact
    /// mirror of the #2554 lower-bound clamp to schedule activation. A job that expired partway
    /// through the scan window keeps the occurrences that fell strictly before <c>ExpiresAt</c>,
    /// because those really were missed; only the ones at or after it are non-events. A job that
    /// expired before the window even opened collapses to an empty window, which is clause 1.</para>
    ///
    /// <para>The bound is exclusive, matching the scan's existing treatment of <c>now</c>, so an
    /// occurrence landing exactly on <c>ExpiresAt</c> is excluded - consistent with the inclusive
    /// <c>&gt;=</c> in <see cref="IsExpired"/>.</para>
    ///
    /// <para><c>ExpiresAt = null</c> returns <paramref name="now"/> unchanged, which is what makes
    /// non-expiring behaviour byte-identical (AC4).</para>
    /// </summary>
    internal static DateTime GetScanCeilingUtc(CronJob job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.ExpiresAt is { } expiresAt && expiresAt < now)
        {
            return expiresAt.UtcDateTime;
        }

        return now.UtcDateTime;
    }
}
