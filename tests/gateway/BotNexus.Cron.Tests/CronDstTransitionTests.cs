using BotNexus.Domain.Primitives;
using Cronos;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression corpus for issue #2810 - daylight-saving transition semantics in the cron
/// next-run computation.
/// <para>
/// The issue was filed as "no DST transition handling on any of six paths", and the premise
/// check found something more precise: Cronos 0.11.1's timezone-aware overloads already
/// implement the correct semantics, but NOTHING in this repository asserted them, and the
/// computation was spelled out independently at seven call sites. An unpinned correct
/// behaviour and an unnoticed wrong one are the same thing to a reviewer, and the specific
/// regression these tests exist to catch is cheap to introduce: dropping the
/// <see cref="TimeZoneInfo"/> argument from one <c>GetNextOccurrence</c> call is a silent
/// one-character-class change that is correct for eleven months of the year.
/// </para>
/// <para>
/// Every instant below is asserted as an absolute UTC instant, never as a local wall-clock
/// time, because a local-time assertion is exactly as ambiguous as the bug.
/// </para>
/// </summary>
public sealed class CronDstTransitionTests
{
    private const string PacificIanaId = "America/Los_Angeles";

    private static TimeZoneInfo Pacific => CronTimeZoneResolver.Resolve(PacificIanaId);

    private static CronExpression Daily(string expression)
        => CronExpression.Parse(expression, CronFormat.Standard);

    private static DateTime Utc(int year, int month, int day, int hour, int minute)
        => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------------------------
    // AC1 - spring forward: the scheduled wall-clock time does not exist that day.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// AC1. On 2026-03-08 America/Los_Angeles jumps 02:00 -> 03:00, so a daily 02:30 job has
    /// no 02:30 that day. It must fire EXACTLY ONCE, at the instant the clock jumps
    /// (03:00 PDT = 10:00Z) - not skipped, and not deferred into a double-fire the next day.
    /// </summary>
    [Fact]
    public void SpringForward_NonexistentLocalTime_FiresExactlyOnceAtTheTransitionInstant()
    {
        var occurrences = Daily("30 2 * * *").RunsBetweenUtc(
            Utc(2026, 3, 7, 0, 0),
            Utc(2026, 3, 10, 0, 0),
            maxRuns: 50,
            Pacific);

        occurrences.ShouldBe(
        [
            Utc(2026, 3, 7, 10, 30),  // 02:30 PST, the day before the transition
            Utc(2026, 3, 8, 10, 0),   // 03:00 PDT - the transition instant, fired once
            Utc(2026, 3, 9, 9, 30),   // 02:30 PDT, the day after
        ]);
    }

    /// <summary>
    /// AC1, stated as the property rather than the instant: the spring-forward day contributes
    /// exactly one occurrence. Asserted separately because a fix that fired zero times on that
    /// day would still satisfy a naive "the list is non-empty" check.
    /// </summary>
    [Fact]
    public void SpringForward_TransitionDay_ContributesExactlyOneOccurrence()
    {
        var occurrences = Daily("30 2 * * *").RunsBetweenUtc(
            Utc(2026, 3, 8, 0, 0),
            Utc(2026, 3, 9, 0, 0),
            maxRuns: 50,
            Pacific);

        occurrences.Count.ShouldBe(1);
    }

    // ---------------------------------------------------------------------------------
    // AC2 - fall back: the scheduled wall-clock time happens twice.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// AC2. On 2026-11-01 America/Los_Angeles repeats 01:00-02:00, so 01:30 occurs twice:
    /// once at 08:30Z (PDT) and again at 09:30Z (PST). A daily 01:30 job must fire ONCE, on
    /// the first pass. Firing on both is a duplicate run - for a maintenance job that is a
    /// duplicated side effect, not merely a wasted turn.
    /// </summary>
    [Fact]
    public void FallBack_AmbiguousLocalTime_FiresExactlyOnceOnTheFirstPass()
    {
        var occurrences = Daily("30 1 * * *").RunsBetweenUtc(
            Utc(2026, 10, 31, 0, 0),
            Utc(2026, 11, 3, 0, 0),
            maxRuns: 50,
            Pacific);

        occurrences.ShouldBe(
        [
            Utc(2026, 10, 31, 8, 30), // 01:30 PDT, the day before
            Utc(2026, 11, 1, 8, 30),  // 01:30 PDT - the FIRST of the two 01:30s
            Utc(2026, 11, 2, 9, 30),  // 01:30 PST, the day after
        ]);

        // The second, standard-time 01:30 is not an occurrence. Pinned explicitly: an
        // off-by-one-pass regression shows up here before it shows up as a count.
        occurrences.ShouldNotContain(Utc(2026, 11, 1, 9, 30));
    }

    /// <summary>
    /// AC2, as the property: the fall-back day contributes exactly one occurrence.
    /// </summary>
    [Fact]
    public void FallBack_TransitionDay_ContributesExactlyOneOccurrence()
    {
        var occurrences = Daily("30 1 * * *").RunsBetweenUtc(
            Utc(2026, 11, 1, 0, 0),
            Utc(2026, 11, 2, 0, 0),
            maxRuns: 50,
            Pacific);

        occurrences.Count.ShouldBe(1);
    }

    /// <summary>
    /// AC2 at hourly granularity, where fall-back adds a real extra hour of wall time: an
    /// hourly job must fire 25 times on the fall-back day, not 24. This is the mirror of the
    /// daily case - the correct behaviour there is "once, not twice", and here it is "every
    /// elapsed hour, including the repeated one". A policy that suppressed the ambiguous hour
    /// wholesale would pass the daily tests and silently drop an hourly run.
    /// </summary>
    [Fact]
    public void FallBack_HourlyJob_FiresOnEveryElapsedHourIncludingTheRepeatedOne()
    {
        var occurrences = Daily("0 * * * *").RunsBetweenUtc(
            Utc(2026, 11, 1, 7, 0),
            Utc(2026, 11, 1, 11, 0),
            maxRuns: 50,
            Pacific);

        // 08:00Z = 01:00 PDT, 09:00Z = 01:00 PST (the repeat), 10:00Z = 02:00 PST.
        occurrences.ShouldBe(
        [
            Utc(2026, 11, 1, 8, 0),
            Utc(2026, 11, 1, 9, 0),
            Utc(2026, 11, 1, 10, 0),
        ]);
    }

    // ---------------------------------------------------------------------------------
    // AC3 - catch-up enumeration agrees with forward scheduling.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// AC3. <c>MissedRunDetectionService</c> enumerates missed runs by advancing a cursor
    /// through history, which is the only next-run computation that necessarily crosses a PAST
    /// transition. This asserts the walk produces the same instants as a single forward range
    /// query over the same window - the property the issue names as "catch-up enumeration
    /// produces the same set of runs as forward scheduling would have".
    /// <para>
    /// Both transition directions and three granularities are covered, because a cursor-walk
    /// defect at a transition typically shows up only when the step size is smaller than the
    /// transition itself.
    /// </para>
    /// </summary>
    [Theory]
    // spring forward
    [InlineData("30 2 * * *", 2026, 3, 6, 2026, 3, 12)]
    [InlineData("0 * * * *", 2026, 3, 8, 2026, 3, 9)]
    [InlineData("*/15 * * * *", 2026, 3, 8, 2026, 3, 8)]
    // fall back
    [InlineData("30 1 * * *", 2026, 10, 30, 2026, 11, 5)]
    [InlineData("0 * * * *", 2026, 11, 1, 2026, 11, 2)]
    [InlineData("*/15 * * * *", 2026, 11, 1, 2026, 11, 1)]
    public void CatchUpWalk_AcrossATransition_MatchesForwardScheduling(
        string schedule,
        int fromYear,
        int fromMonth,
        int fromDay,
        int toYear,
        int toMonth,
        int toDay)
    {
        var expression = Daily(schedule);
        var from = Utc(fromYear, fromMonth, fromDay, 0, 0);
        // The */15 cases deliberately span a single day; widen the others by one day so the
        // window always contains the transition rather than ending on it.
        var to = Utc(toYear, toMonth, toDay, 0, 0).AddDays(1);

        var walked = expression.RunsBetweenUtc( from, to, maxRuns: 500, Pacific);

        var forward = expression
            .GetOccurrences(from, to, Pacific, fromInclusive: false, toInclusive: false)
            .ToList();

        // Non-vacuity: an empty window would make the equality assertion meaningless.
        forward.ShouldNotBeEmpty();
        walked.ShouldBe(forward);
    }

    /// <summary>
    /// AC3, end to end through the production entry point rather than the helper: a job whose
    /// last run was before the spring-forward transition and which is being caught up after it
    /// must report exactly the occurrences that forward scheduling would have produced -
    /// including the single transition-instant run on 2026-03-08.
    /// </summary>
    [Fact]
    public void MissedRunDetection_AcrossSpringForward_ReportsTheForwardScheduleExactly()
    {
        var job = PacificDailyJob("30 2 * * *", lastRunUtc: Utc(2026, 3, 6, 10, 30));

        var missed = MissedRunDetectionService.GetMissedRuns(job, new DateTimeOffset(Utc(2026, 3, 10, 0, 0)));

        missed.Select(m => m.UtcDateTime).ShouldBe(
        [
            Utc(2026, 3, 7, 10, 30),
            Utc(2026, 3, 8, 10, 0),
            Utc(2026, 3, 9, 9, 30),
        ]);
    }

    /// <summary>
    /// AC3 for the other direction: catching up across fall-back must not replay the ambiguous
    /// hour as two runs. A duplicated catch-up run is the more damaging of the two symptoms -
    /// it re-executes a side effect that already happened.
    /// </summary>
    [Fact]
    public void MissedRunDetection_AcrossFallBack_DoesNotDuplicateTheAmbiguousRun()
    {
        var job = PacificDailyJob("30 1 * * *", lastRunUtc: Utc(2026, 10, 31, 8, 30));

        var missed = MissedRunDetectionService.GetMissedRuns(job, new DateTimeOffset(Utc(2026, 11, 3, 0, 0)));

        missed.Select(m => m.UtcDateTime).ShouldBe(
        [
            Utc(2026, 11, 1, 8, 30),
            Utc(2026, 11, 2, 9, 30),
        ]);
    }

    // ---------------------------------------------------------------------------------
    // The regression the tests above actually guard: losing the timezone argument.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The concrete failure mode. Computing the same schedule without the timezone yields a
    /// different instant either side of a transition, so this pins that the timezone-aware
    /// computation is genuinely doing work. Without it, every assertion above could be
    /// satisfied by a UTC computation on a UTC host and nobody would notice until March.
    /// </summary>
    [Fact]
    public void NextRun_WithoutTheTimeZone_DiffersAcrossTheTransition_SoTheZoneIsLoadBearing()
    {
        var expression = Daily("30 2 * * *");
        var after = new DateTimeOffset(Utc(2026, 3, 7, 12, 0));

        var zoned = expression.NextRun(after, Pacific);
        var utc = expression.NextRun(after, TimeZoneInfo.Utc);

        zoned.ShouldBe(new DateTimeOffset(Utc(2026, 3, 8, 10, 0)));
        utc.ShouldBe(new DateTimeOffset(Utc(2026, 3, 8, 2, 30)));
        zoned.ShouldNotBe(utc);
    }

    // ---------------------------------------------------------------------------------
    // Cursor-kind robustness - the second thing every call site has to get right.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Cronos throws <see cref="ArgumentException"/> for a <see cref="DateTime"/> cursor whose
    /// kind is not <see cref="DateTimeKind.Utc"/>. A cursor that lost its kind while being
    /// carried through the catch-up loop would therefore throw from inside a background
    /// service and abort catch-up for every job, not just the one with the odd cursor. The
    /// calculator normalises instead.
    /// </summary>
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void GetNextOccurrenceUtc_NormalisesTheCursorKind_InsteadOfThrowing(DateTimeKind kind)
    {
        var cursor = DateTime.SpecifyKind(new DateTime(2026, 3, 7, 12, 0, 0), kind);

        var next = Daily("30 2 * * *").NextRunUtc(cursor, Pacific);

        next.ShouldBe(Utc(2026, 3, 8, 10, 0));
    }

    /// <summary>
    /// A <see cref="DateTimeKind.Local"/> cursor is CONVERTED rather than reinterpreted -
    /// reinterpreting a local instant as UTC would move the job by the host's offset, which is
    /// a silent scheduling error rather than a loud one. Asserted against a value derived from
    /// the host offset so it holds on any CI machine.
    /// </summary>
    [Fact]
    public void GetNextOccurrenceUtc_LocalCursor_IsConvertedNotReinterpreted()
    {
        var utcCursor = Utc(2026, 3, 7, 12, 0);
        var localCursor = utcCursor.ToLocalTime();

        var fromLocal = Daily("30 2 * * *").NextRunUtc(localCursor, Pacific);
        var fromUtc = Daily("30 2 * * *").NextRunUtc(utcCursor, Pacific);

        fromLocal.ShouldBe(fromUtc);
    }

    private static CronJob PacificDailyJob(string schedule, DateTime lastRunUtc) => new()
    {
        Id = JobId.From("dst-job"),
        Name = "DST job",
        Schedule = schedule,
        TimeZone = PacificIanaId,
        ActionType = "agent-prompt",
        AgentId = AgentId.From("test-agent"),
        Enabled = true,
        CreatedBy = "test",
        CreatedAt = new DateTimeOffset(Utc(2026, 1, 1, 0, 0)),
        LastRunAt = new DateTimeOffset(lastRunUtc),
    };
}
