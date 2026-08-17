namespace BotNexus.Cron;

using BotNexus.Domain.Primitives;

/// <summary>
/// Per-job cost rollup derived from <c>cron_runs</c> at read time (#2641).
/// </summary>
/// <remarks>
/// <para>
/// Derived, never stored. #2557 established deriving a streak from run history rather than
/// maintaining a counter on <c>cron_jobs</c>: a stored aggregate is state that every write path must
/// remember to keep current, and the one that forgets is invisible until the number is already wrong.
/// </para>
/// <para>
/// <b>Total is the feature.</b> A per-run average alone gets the ranking backwards for the exact
/// shape present in the live data: the most expensive job per run fires 8 times a week while a job
/// four times cheaper per run fires 193 times and is the platform's largest consumer. Totals are
/// therefore computed as per-run cost summed across the window (equivalently, average x frequency),
/// and <see cref="TotalTokens"/> / <see cref="TotalToolCalls"/> / <see cref="TotalTurns"/> are the
/// sortable columns.
/// </para>
/// </remarks>
public sealed record CronJobCostRollup
{
    /// <summary>The job this rollup describes.</summary>
    public required JobId JobId { get; init; }

    /// <summary>Number of runs of this job inside the window, including runs that measured nothing.</summary>
    public required int RunCount { get; init; }

    /// <summary>
    /// Number of runs inside the window that carried at least one token measurement. Distinct from
    /// <see cref="RunCount"/> so a caller can tell "cheap" from "unmeasured": averaging a measured
    /// total over ALL runs would silently dilute a real figure with runs that never reported one.
    /// </summary>
    public required int MeasuredRunCount { get; init; }

    /// <summary>Total provider-reported tokens across measured runs, or null when nothing was measured.</summary>
    public long? TotalTokens { get; init; }

    /// <summary>Total tool invocations across measured runs, or null when nothing was measured.</summary>
    public long? TotalToolCalls { get; init; }

    /// <summary>Total model turns across measured runs, or null when nothing was measured.</summary>
    public long? TotalTurns { get; init; }

    /// <summary>Total wall-clock duration in milliseconds across measured runs, or null when nothing was measured.</summary>
    public long? TotalDurationMs { get; init; }

    /// <summary>
    /// Mean tokens per <b>measured</b> run, or null when no run in the window reported tokens.
    /// Divided by <see cref="MeasuredRunCount"/>, never by <see cref="RunCount"/>.
    /// </summary>
    public double? AverageTokensPerRun => Average(TotalTokens);

    /// <summary>Mean tool invocations per measured run, or null when unmeasured.</summary>
    public double? AverageToolCallsPerRun => Average(TotalToolCalls);

    /// <summary>Mean model turns per measured run, or null when unmeasured.</summary>
    public double? AverageTurnsPerRun => Average(TotalTurns);

    /// <summary>Mean wall-clock duration in milliseconds per measured run, or null when unmeasured.</summary>
    public double? AverageDurationMsPerRun => Average(TotalDurationMs);

    /// <summary>
    /// Start of the window this rollup covers (inclusive), after retention reconciliation.
    /// </summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>
    /// Number of days actually covered, which is the requested window clamped to the configured
    /// run retention. See <see cref="WindowTruncatedByRetention"/>.
    /// </summary>
    public required int WindowDays { get; init; }

    /// <summary>
    /// True when the caller asked for a window longer than <c>CronRunRetentionOptions.RetentionDays</c>
    /// and the window was clamped (#2641 AC6). The retention service purges older runs, so an
    /// unclamped longer window would report a total that silently omits the purged runs while
    /// looking exactly like a complete one. Surfacing the clamp is the difference between a bounded
    /// number and a wrong number.
    /// </summary>
    public required bool WindowTruncatedByRetention { get; init; }

    private double? Average(long? total)
        => total is null || MeasuredRunCount <= 0 ? null : (double)total.Value / MeasuredRunCount;
}
