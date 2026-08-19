using System.Globalization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Wire shape of one per-job cron cost rollup as served by <c>GET /api/cron/costs</c> (#2641).
/// </summary>
/// <remarks>
/// <para>
/// This mirrors <c>BotNexus.Cron.CronJobCostRollup</c> and adds nothing to it: #3289 is a
/// presentation layer over a measurement seam that is already complete, so there is no new
/// endpoint, controller action, store method or persisted column behind this type.
/// </para>
/// <para>
/// Every total is nullable and <see langword="null"/> means <em>not measured</em>, never zero
/// (#2554, restated verbatim in <c>CronRunCost</c>'s remarks). A <c>command</c> or <c>webhook</c>
/// job has no turn or token concept at all; coercing it to <c>0</c> would present "we did not look"
/// as "this job is free" and invert the exact ranking this view exists to establish.
/// </para>
/// </remarks>
/// <param name="JobId">The job this rollup describes.</param>
/// <param name="RunCount">Runs inside the window, including runs that measured nothing.</param>
/// <param name="MeasuredRunCount">Runs inside the window that carried at least one measurement.</param>
/// <param name="TotalTokens">Total provider tokens across measured runs, or null when unmeasured.</param>
/// <param name="TotalToolCalls">Total tool invocations across measured runs, or null when unmeasured.</param>
/// <param name="TotalTurns">Total model turns across measured runs, or null when unmeasured.</param>
/// <param name="TotalDurationMs">Total wall-clock milliseconds across measured runs, or null when unmeasured.</param>
/// <param name="WindowStart">Start of the covered window, after retention reconciliation.</param>
/// <param name="WindowDays">Days actually covered - the requested window clamped to run retention.</param>
/// <param name="WindowTruncatedByRetention">True when the requested window exceeded run retention and was clamped.</param>
public sealed record CronJobCostDto(
    string JobId,
    int RunCount,
    int MeasuredRunCount,
    long? TotalTokens = null,
    long? TotalToolCalls = null,
    long? TotalTurns = null,
    long? TotalDurationMs = null,
    DateTimeOffset WindowStart = default,
    int WindowDays = 0,
    bool WindowTruncatedByRetention = false)
{
    /// <summary>
    /// Mean tokens per <b>measured</b> run, or null when nothing was measured.
    /// </summary>
    /// <remarks>
    /// Recomputed client-side from the totals rather than read off a serialized computed property,
    /// and divided by <see cref="MeasuredRunCount"/> - never by <see cref="RunCount"/>. Dividing by
    /// the run count would dilute a real figure with runs that never reported one, which is the
    /// distinction <see cref="MeasuredRunCount"/> exists to preserve.
    /// </remarks>
    public double? AverageTokensPerRun => Average(TotalTokens);

    /// <summary>Mean tool invocations per measured run, or null when unmeasured.</summary>
    public double? AverageToolCallsPerRun => Average(TotalToolCalls);

    /// <summary>Mean model turns per measured run, or null when unmeasured.</summary>
    public double? AverageTurnsPerRun => Average(TotalTurns);

    /// <summary>Mean wall-clock milliseconds per measured run, or null when unmeasured.</summary>
    public double? AverageDurationMsPerRun => Average(TotalDurationMs);

    private double? Average(long? total)
        => total is null || MeasuredRunCount <= 0 ? null : (double)total.Value / MeasuredRunCount;
}

/// <summary>
/// One row of the Activity page's cron cost subsection (#3289): a rollup plus the job's display
/// name and the derived efficiency signal the subsection exists to surface.
/// </summary>
/// <param name="Cost">The rollup as served by the gateway - the single source of every number.</param>
/// <param name="JobName">
/// Display name resolved from the cron job list, falling back to the job id when the job is no
/// longer listed (a rollup can outlive its job inside the retention window).
/// </param>
public sealed record CronCostRow(CronJobCostDto Cost, string JobName)
{
    /// <summary>The job this row addresses. Delegated so it cannot disagree with <see cref="Cost"/>.</summary>
    public string JobId => Cost.JobId;

    /// <summary>
    /// Tool calls per model turn - the one value this subsection derives rather than transcribes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Total tokens tells a reader what a job costs; this tells them <em>why</em>. A job averaging
    /// many tool calls per turn is one where the agent is groping - re-reading files, retrying a
    /// malformed command, paginating something a single query would have returned. That is a skill
    /// or script defect and it is fixable in a way that "this job is expensive" alone is not.
    /// </para>
    /// <para>
    /// <see langword="null"/> - i.e. ABSENT, never <c>0</c>, never <c>NaN</c>, never a throw - when
    /// either input is unmeasured or when turns are zero. A ratio over zero turns is not a small
    /// number, it is an undefined one, and rendering it as <c>0</c> would rank the least-measurable
    /// jobs as the most efficient ones.
    /// </para>
    /// </remarks>
    public double? ToolCallsPerTurn =>
        CronCostProjection.ToolCallsPerTurn(Cost.AverageToolCallsPerRun, Cost.AverageTurnsPerRun);
}

/// <summary>
/// Pure projection for the Activity page's cron cost subsection (#3289). Static and
/// dependency-free, mirroring <see cref="ActivityCostProjection"/>, so the ranking and the derived
/// efficiency signal are unit-testable without bUnit.
/// </summary>
public static class CronCostProjection
{
    /// <summary>
    /// Rendered text for a value the platform did not measure. Deliberately a <em>word</em> rather
    /// than a dash or a zero: the entire point of the nullable model is that "we did not look" reads
    /// differently from "we looked and it was none".
    /// </summary>
    public const string NotMeasured = "not measured";

    /// <summary>
    /// Ranks cron cost rollups for display, most expensive TOTAL first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Total, not per-run average.</b> <c>CronJobCostRollup</c>'s own remarks establish why: a
    /// job costing a quarter as much per run but firing 24x more often is the platform's larger
    /// consumer, and a per-run figure alone reports it as the cheaper one. Both figures are shown;
    /// total is the sort key.
    /// </para>
    /// <para>
    /// A rollup with no measured total sorts <b>last</b> rather than as a zero - unmeasured is
    /// unknown, not cheap. Run count then job id break ties so the order is total and equal rows
    /// never reshuffle between reads.
    /// </para>
    /// </remarks>
    /// <param name="costs">Rollups as returned by <c>GET /api/cron/costs</c>.</param>
    /// <param name="jobs">Cron jobs used only to resolve display names; may be empty.</param>
    public static IReadOnlyList<CronCostRow> Project(
        IEnumerable<CronJobCostDto> costs,
        IEnumerable<CronJobDto>? jobs)
    {
        ArgumentNullException.ThrowIfNull(costs);

        var namesById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var job in jobs ?? [])
        {
            if (!string.IsNullOrWhiteSpace(job.Id) && !namesById.ContainsKey(job.Id))
                namesById[job.Id] = string.IsNullOrWhiteSpace(job.Name) ? job.Id : job.Name;
        }

        // First rollup for a job id wins, matching the de-duplication rule the conversation cost
        // projection already uses: a server that repeated a job id must still yield one row.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<CronCostRow>();
        foreach (var cost in costs)
        {
            if (!seen.Add(cost.JobId))
                continue;
            rows.Add(new CronCostRow(
                cost,
                namesById.TryGetValue(cost.JobId, out var name) ? name : cost.JobId));
        }

        return rows
            .OrderByDescending(r => r.Cost.TotalTokens.HasValue)
            .ThenByDescending(r => r.Cost.TotalTokens ?? 0)
            .ThenByDescending(r => r.Cost.TotalToolCalls ?? 0)
            .ThenByDescending(r => r.Cost.RunCount)
            .ThenBy(r => r.JobId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The derived efficiency signal: tool calls per model turn, or <see langword="null"/> when it
    /// is not defined (#3289 AC4).
    /// </summary>
    /// <remarks>
    /// Returns null - never <c>0</c>, never <c>NaN</c>, never a division-by-zero throw - when either
    /// input is unmeasured or when turns are zero. Guarding the divisor explicitly matters even
    /// though IEEE double division would not throw: <c>x / 0.0</c> yields Infinity or NaN, which
    /// formats as text no reader can act on and sorts unpredictably.
    /// </remarks>
    /// <param name="averageToolCallsPerRun">Mean tool invocations per measured run, or null.</param>
    /// <param name="averageTurnsPerRun">Mean model turns per measured run, or null.</param>
    public static double? ToolCallsPerTurn(double? averageToolCallsPerRun, double? averageTurnsPerRun)
    {
        if (averageToolCallsPerRun is not { } toolCalls || averageTurnsPerRun is not { } turns)
            return null;
        if (turns <= 0 || double.IsNaN(turns) || double.IsNaN(toolCalls))
            return null;

        var ratio = toolCalls / turns;
        return double.IsFinite(ratio) ? ratio : null;
    }

    /// <summary>
    /// Renders a possibly-unmeasured count, keeping the null/zero distinction visible at the render
    /// layer as well as in the model (#3289 AC5).
    /// </summary>
    /// <param name="value">The count, or null when unmeasured.</param>
    public static string FormatCount(long? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? NotMeasured;

    /// <summary>Renders a possibly-unmeasured per-run average to one decimal place.</summary>
    /// <param name="value">The average, or null when unmeasured.</param>
    public static string FormatAverage(double? value) =>
        value?.ToString("N1", CultureInfo.InvariantCulture) ?? NotMeasured;

    /// <summary>Renders a duration in milliseconds as seconds, or the not-measured word.</summary>
    /// <param name="milliseconds">Duration in milliseconds, or null when unmeasured.</param>
    public static string FormatDuration(long? milliseconds) =>
        milliseconds is { } ms
            ? (ms / 1000d).ToString("N1", CultureInfo.InvariantCulture) + "s"
            : NotMeasured;

    /// <summary>
    /// Renders the derived efficiency signal, or the not-measured word when it is undefined.
    /// </summary>
    /// <param name="value">Tool calls per turn, or null.</param>
    public static string FormatRatio(double? value) =>
        value?.ToString("N2", CultureInfo.InvariantCulture) ?? NotMeasured;

    /// <summary>
    /// True when any rollup in the response reports its window was clamped to run retention, so the
    /// UI can state the totals are bounded (#3289 AC7).
    /// </summary>
    /// <remarks>
    /// A truncated total that looks complete is worse than a visibly bounded one - the notice is the
    /// difference between a bounded number and a wrong number.
    /// </remarks>
    /// <param name="rows">The projected rows.</param>
    public static bool WindowTruncated(IEnumerable<CronCostRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Any(r => r.Cost.WindowTruncatedByRetention);
    }

    /// <summary>
    /// The effective window in days reported by the response, or null when it reported none.
    /// </summary>
    /// <param name="rows">The projected rows.</param>
    public static int? EffectiveWindowDays(IEnumerable<CronCostRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            if (row.Cost.WindowDays > 0)
                return row.Cost.WindowDays;
        }
        return null;
    }

    /// <summary>
    /// The navigation target for a cron cost row, keyed on the row's OWN job id rather than its
    /// display position (#3289 AC8), so a re-sort can never send a reader to a different job than
    /// the one they clicked.
    /// </summary>
    /// <param name="row">A projected cost row.</param>
    public static string NavigationTarget(CronCostRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"/cron?jobId={Uri.EscapeDataString(row.JobId)}";
    }
}
