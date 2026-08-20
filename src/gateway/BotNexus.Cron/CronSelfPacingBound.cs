namespace BotNexus.Cron;

/// <summary>
/// #3338 clauses 6-7: THE single decision for how far into the future a self-pacing loop may set its
/// own next wake, and the single place that records whether the bound was actually hit.
/// </summary>
/// <remarks>
/// <para>
/// A loop that proposes its own next check is a runaway-cost surface in both directions: a floor of
/// zero lets a job re-fire continuously, and an absent ceiling lets a job silently park itself a year
/// out and look "scheduled" while doing nothing. The clamp is therefore load-bearing, not a nicety.
/// </para>
/// <para>
/// The clamp is also OBSERVABLE by construction. <see cref="Clamp"/> returns the requested delay
/// alongside the effective one and a <see cref="CronSelfPacingDecision.WasClamped"/> flag, so a loop
/// pinned at the floor cannot masquerade as one pacing itself. #3244 is the precedent: a bound whose
/// application is invisible cannot be reasoned about, and therefore cannot be fixed - so the decision
/// type deliberately makes it impossible to report the effective value without the requested one.
/// </para>
/// </remarks>
public static class CronSelfPacingBound
{
    /// <summary>Default floor: one minute, matching the scheduler's own minimum tick granularity.</summary>
    public static readonly TimeSpan DefaultFloor = TimeSpan.FromMinutes(1);

    /// <summary>Default ceiling: one hour. Beyond this a loop should carry an explicit schedule.</summary>
    public static readonly TimeSpan DefaultCeiling = TimeSpan.FromHours(1);

    /// <summary>
    /// Clamps a proposed next-wake delay into <c>[floor, ceiling]</c>.
    /// </summary>
    /// <param name="requested">The delay the agent proposed.</param>
    /// <param name="floor">Lower bound; a non-positive value degrades to <see cref="DefaultFloor"/>.</param>
    /// <param name="ceiling">Upper bound; a value at or below the floor degrades to <c>floor</c>.</param>
    /// <remarks>
    /// Misconfiguration degrades to the defaults rather than throwing: a bad config value must not be
    /// able to disable the bound entirely, which is the failure mode the clamp exists to prevent.
    /// </remarks>
    public static CronSelfPacingDecision Clamp(TimeSpan requested, TimeSpan? floor = null, TimeSpan? ceiling = null)
    {
        var effectiveFloor = floor is { } f && f > TimeSpan.Zero ? f : DefaultFloor;
        var effectiveCeiling = ceiling is { } c && c > effectiveFloor ? c : MaxOf(effectiveFloor, DefaultCeiling);
        if (effectiveCeiling < effectiveFloor)
            effectiveCeiling = effectiveFloor;

        if (requested < effectiveFloor)
            return new CronSelfPacingDecision(requested, effectiveFloor, effectiveFloor, effectiveCeiling, ClampReason.Floor);

        if (requested > effectiveCeiling)
            return new CronSelfPacingDecision(requested, effectiveCeiling, effectiveFloor, effectiveCeiling, ClampReason.Ceiling);

        return new CronSelfPacingDecision(requested, requested, effectiveFloor, effectiveCeiling, ClampReason.None);
    }

    private static TimeSpan MaxOf(TimeSpan left, TimeSpan right) => left > right ? left : right;

    /// <summary>Which bound, if any, was applied. Distinct values so "pinned low" and "pinned high" never share a symbol.</summary>
    public enum ClampReason
    {
        /// <summary>The request was already inside the bound and was honoured verbatim.</summary>
        None,

        /// <summary>The request was below the floor and was raised to it.</summary>
        Floor,

        /// <summary>The request was above the ceiling and was lowered to it.</summary>
        Ceiling
    }
}

/// <summary>
/// The outcome of a self-pacing clamp (#3338 clause 7). Carries the REQUESTED value beside the
/// EFFECTIVE one so every caller reporting the result reports both, by construction.
/// </summary>
/// <param name="Requested">What the agent asked for.</param>
/// <param name="Effective">What it actually gets.</param>
/// <param name="Floor">The floor in force for this decision.</param>
/// <param name="Ceiling">The ceiling in force for this decision.</param>
/// <param name="Reason">Which bound was applied, if any.</param>
public sealed record CronSelfPacingDecision(
    TimeSpan Requested,
    TimeSpan Effective,
    TimeSpan Floor,
    TimeSpan Ceiling,
    CronSelfPacingBound.ClampReason Reason)
{
    /// <summary>True when the requested value was not honoured verbatim.</summary>
    public bool WasClamped => Reason != CronSelfPacingBound.ClampReason.None;
}
