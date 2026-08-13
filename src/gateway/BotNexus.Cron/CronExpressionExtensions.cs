using Cronos;

namespace BotNexus.Cron;

/// <summary>
/// The single canonical definition of "when does this cron job next run", including its
/// behaviour across daylight-saving transitions, expressed as extension methods so a next-run
/// question reads as a question asked OF a <see cref="CronExpression"/>:
/// <c>expression.NextRun(now, tz)</c>.
/// <para>
/// These are extension methods rather than instance methods because <see cref="CronExpression"/>
/// is Cronos' sealed type, not ours - extending it in place is not available, and wrapping it in
/// a bespoke value type would add a second thing to keep in sync with the schedule. Extensions
/// give the instance-call shape at every site without owning the type.
/// </para>
/// <para>
/// Issue #2810: the repository computed next-run times at seven independent
/// <c>CronExpression.GetNextOccurrence</c> call sites (scheduler due-scan, scheduler
/// reschedule, missed-run enumeration, missed-run truncation probe, cron tool create,
/// cron tool update, and the REST controller's definition-update path) with no shared
/// statement of transition policy. Nothing was wrong at any single site, but nothing
/// recorded WHY it was right either, so the policy could be changed at one site without
/// anything noticing - and the catch-up walk in
/// <see cref="MissedRunDetectionService"/> re-enters the computation with a historical
/// cursor rather than <c>now</c>, which is exactly the "historical timezone transition"
/// case that motivated the issue.
/// </para>
/// <para>
/// <b>The policy, verified against Cronos 0.11.1 rather than assumed.</b> Cronos'
/// timezone-aware overloads already implement the correct semantics; these methods exist to
/// state them once, pin them with tests, and stop each site re-deriving them:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Spring forward (invalid local time).</b> A daily <c>30 2 * * *</c> job in
/// <c>America/Los_Angeles</c> has no 02:30 on 2026-03-08. Cronos fires it EXACTLY ONCE, at
/// the instant the clock jumps - 03:00 local (10:00Z) - rather than skipping the day or
/// firing twice. The job is neither lost nor duplicated.
/// </item>
/// <item>
/// <b>Fall back (ambiguous local time).</b> A daily <c>30 1 * * *</c> job has two 01:30s on
/// 2026-11-01. Cronos fires it EXACTLY ONCE, on the FIRST (daylight-time, -07:00) pass.
/// The second, standard-time 01:30 is not a second occurrence.
/// </item>
/// <item>
/// <b>Catch-up equals forward scheduling.</b> Iteratively advancing a cursor through
/// history - which is what missed-run enumeration does - produces exactly the same instant
/// set as a single forward range enumeration over the same window, across both transition
/// directions and at daily, hourly and sub-hourly granularity.
/// </item>
/// </list>
/// <para>
/// <b>The two things that DO have to be got right at every site</b>, and are the reason this
/// is shared code rather than a comment:
/// </para>
/// <list type="number">
/// <item>
/// The timezone-aware overload must actually be used. Dropping the
/// <see cref="TimeZoneInfo"/> argument computes the schedule in UTC, which is silently
/// correct for eleven months of the year and wrong by an hour either side of a transition.
/// Making the zone a required parameter of every method here removes the overload that made
/// that mistake expressible.
/// </item>
/// <item>
/// A <see cref="DateTime"/> cursor handed to Cronos must be <see cref="DateTimeKind.Utc"/>.
/// Cronos throws <see cref="ArgumentException"/> for <c>Unspecified</c> and <c>Local</c>
/// kinds, so a cursor that loses its kind while being carried through the catch-up loop
/// turns a scheduling question into an exception inside a background service.
/// </item>
/// </list>
/// <para>
/// <b>Naming note (load-bearing).</b> These deliberately do NOT reuse Cronos' verb
/// <c>GetNextOccurrence</c>. <c>CronNextRunSingleDefinitionTests</c> fences the invariant that
/// only this file calls Cronos' occurrence API directly, and it does so by scanning source text;
/// an extension sharing the name would make a compliant <c>expression.GetNextOccurrence(now, tz)</c>
/// textually indistinguishable from the raw call it is meant to forbid. Distinct verbs keep the
/// fence able to tell the remedy from the violation.
/// </para>
/// </summary>
internal static class CronExpressionExtensions
{
    /// <summary>
    /// The next run strictly after <paramref name="after"/>, in the job's timezone,
    /// or <see langword="null"/> when the expression has no further occurrence.
    /// </summary>
    internal static DateTimeOffset? NextRun(
        this CronExpression expression,
        DateTimeOffset after,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(timeZone);
        return expression.GetNextOccurrence(after, timeZone);
    }

    /// <summary>
    /// The next run strictly after a UTC <paramref name="afterUtc"/> cursor.
    /// <para>
    /// The kind is normalised rather than asserted: a cursor may have travelled through a
    /// <see cref="DateTimeOffset"/> conversion or a persisted column on its way here, and an
    /// <see cref="ArgumentException"/> thrown from inside the missed-run background loop
    /// would abort catch-up for every job, not just the one with the odd cursor.
    /// </para>
    /// </summary>
    internal static DateTime? NextRunUtc(
        this CronExpression expression,
        DateTime afterUtc,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(timeZone);
        return expression.GetNextOccurrence(NormaliseToUtcKind(afterUtc), timeZone);
    }

    /// <summary>
    /// Every run in the half-open UTC window (<paramref name="afterUtc"/>,
    /// <paramref name="beforeUtc"/>), capped at <paramref name="maxRuns"/>.
    /// <para>
    /// This is the catch-up walk. It is a cursor loop rather than a range query because the
    /// caller needs the cap to bound iteration after long downtime, but it is defined HERE so
    /// the walk and the forward computation can be pinned as producing the same instants
    /// across a historical transition (#2810 clause 3) instead of being two independent loops
    /// that happen to agree today.
    /// </para>
    /// </summary>
    internal static List<DateTime> RunsBetweenUtc(
        this CronExpression expression,
        DateTime afterUtc,
        DateTime beforeUtc,
        int maxRuns,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(timeZone);
        var runs = new List<DateTime>();
        if (maxRuns <= 0)
            return runs;
        var cursor = NormaliseToUtcKind(afterUtc);
        var limit = NormaliseToUtcKind(beforeUtc);
        while (runs.Count < maxRuns)
        {
            var next = expression.NextRunUtc(cursor, timeZone);
            if (next is null || next.Value >= limit)
                break;
            runs.Add(next.Value);
            cursor = next.Value;
        }
        return runs;
    }

    /// <summary>
    /// Reinterprets a cursor as UTC. <c>Unspecified</c> is treated as already-UTC (every
    /// producer in the cron seam works in UTC); <c>Local</c> is converted, because
    /// reinterpreting a local instant as UTC would move the job by the host's offset.
    /// </summary>
    private static DateTime NormaliseToUtcKind(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
