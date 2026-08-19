using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit tests for the cron cost projection behind the Activity page's <c>cron</c> subsection
/// (#3289). These pin the total-not-average ranking inversion, the derived tool-calls-per-turn
/// signal and its undefined cases, the null-is-not-zero rule and the id-keyed navigation target,
/// without needing bUnit.
/// </summary>
public sealed class CronCostProjectionTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    private static CronJobCostDto Cost(
        string jobId,
        int runs,
        int measuredRuns,
        long? tokens = null,
        long? toolCalls = null,
        long? turns = null,
        long? durationMs = null,
        int windowDays = 7,
        bool truncated = false) =>
        new(
            JobId: jobId,
            RunCount: runs,
            MeasuredRunCount: measuredRuns,
            TotalTokens: tokens,
            TotalToolCalls: toolCalls,
            TotalTurns: turns,
            TotalDurationMs: durationMs,
            WindowStart: WindowStart,
            WindowDays: windowDays,
            WindowTruncatedByRetention: truncated);

    private static CronJobDto Job(string id, string name) => new() { Id = id, Name = name };

    // ── AC3: ranked by TOTAL, not per-run average ──────────────────────────

    /// <summary>
    /// AC3: the exact inversion <c>CronJobCostRollup</c> warns about. <c>frequent</c> costs a
    /// QUARTER as much per run as <c>rare</c> but fires 24x more often, so it is the platform's
    /// larger consumer. A projection ranking by per-run average would put <c>rare</c> first.
    /// </summary>
    [Fact]
    public void Job_with_lower_per_run_average_but_higher_total_outranks_the_expensive_one()
    {
        IReadOnlyList<CronJobCostDto> costs =
        [
            // 8 runs x 100,000 tokens each = 800,000 total, the highest per-run figure.
            Cost("rare", runs: 8, measuredRuns: 8, tokens: 800_000, toolCalls: 80, turns: 40),
            // 192 runs x 25,000 tokens each = 4,800,000 total, a quarter the per-run cost.
            Cost("frequent", runs: 192, measuredRuns: 192, tokens: 4_800_000, toolCalls: 3_840, turns: 1_920)
        ];

        var rows = CronCostProjection.Project(costs, [Job("rare", "Rare"), Job("frequent", "Frequent")]);

        // The fixture really does invert: per-run, "rare" is the more expensive job.
        rows.Single(r => r.JobId == "rare").Cost.AverageTokensPerRun!.Value
            .ShouldBeGreaterThan(rows.Single(r => r.JobId == "frequent").Cost.AverageTokensPerRun!.Value);

        // ...and yet total ranking puts the frequent job first. That is the whole clause.
        rows.Select(r => r.JobId).ShouldBe(["frequent", "rare"]);
        rows.Select(r => r.Cost.TotalTokens ?? 0).ShouldBeInOrder(SortDirection.Descending);
    }

    /// <summary>
    /// AC3 / AC5: a rollup with NO measured total sorts LAST rather than as a zero, even when it has
    /// by far the most runs. Unmeasured is unknown, not cheap.
    /// </summary>
    [Fact]
    public void Unmeasured_total_sorts_last_rather_than_as_zero()
    {
        IReadOnlyList<CronJobCostDto> costs =
        [
            Cost("unmeasured-busy", runs: 5_000, measuredRuns: 0),
            Cost("measured-tiny", runs: 1, measuredRuns: 1, tokens: 10, toolCalls: 1, turns: 1)
        ];

        var rows = CronCostProjection.Project(costs, jobs: null);

        rows.Select(r => r.JobId).ShouldBe(["measured-tiny", "unmeasured-busy"]);
        rows[1].Cost.TotalTokens.ShouldBeNull();
    }

    // ── AC4: the derived tool-calls-per-turn signal ────────────────────────

    /// <summary>
    /// AC4 (happy path): the derived ratio is pinned against a fixture. 3,840 tool calls over 1,920
    /// turns across 192 measured runs is exactly 2 tool calls per turn.
    /// </summary>
    [Fact]
    public void Tool_calls_per_turn_is_computed_from_the_per_run_averages()
    {
        var row = CronCostProjection
            .Project([Cost("j", runs: 192, measuredRuns: 192, tokens: 1, toolCalls: 3_840, turns: 1_920)], null)
            .ShouldHaveSingleItem();

        row.Cost.AverageToolCallsPerRun.ShouldBe(20d);
        row.Cost.AverageTurnsPerRun.ShouldBe(10d);
        row.ToolCallsPerTurn.ShouldNotBeNull();
        row.ToolCallsPerTurn!.Value.ShouldBe(2d, tolerance: 1e-9);
        CronCostProjection.FormatRatio(row.ToolCallsPerTurn).ShouldBe("2.00");
    }

    /// <summary>
    /// AC4 (the load-bearing half): the ratio is ABSENT - not <c>0</c>, not <c>NaN</c>, not a
    /// division-by-zero throw - whenever either input is null or turns are zero. Each case is
    /// asserted separately so a guard that covers only one of them still reddens.
    /// </summary>
    [Fact]
    public void Tool_calls_per_turn_is_absent_when_an_input_is_null_or_turns_are_zero()
    {
        // Null tool calls.
        CronCostProjection.ToolCallsPerTurn(null, 10d).ShouldBeNull();
        // Null turns.
        CronCostProjection.ToolCallsPerTurn(20d, null).ShouldBeNull();
        // Both null.
        CronCostProjection.ToolCallsPerTurn(null, null).ShouldBeNull();
        // Zero turns - the division-by-zero case. IEEE would yield Infinity, not a throw, and
        // Infinity formats as text no reader can act on.
        CronCostProjection.ToolCallsPerTurn(20d, 0d).ShouldBeNull();
        // Negative turns cannot occur but must not produce a negative ratio if they ever did.
        CronCostProjection.ToolCallsPerTurn(20d, -1d).ShouldBeNull();
        // 0/0 - the NaN case.
        CronCostProjection.ToolCallsPerTurn(0d, 0d).ShouldBeNull();

        // Explicitly NOT zero: a null result must be distinguishable from a genuine zero ratio.
        CronCostProjection.ToolCallsPerTurn(20d, 0d).ShouldNotBe(0d);
        // A genuine zero (a job that took turns and made no tool calls) IS reported as zero.
        CronCostProjection.ToolCallsPerTurn(0d, 10d).ShouldBe(0d);

        // ...and at the row layer, through the real rollup shape: a command job measured runs but
        // has no turn or token concept at all.
        var command = CronCostProjection
            .Project([Cost("command-job", runs: 40, measuredRuns: 0)], null)
            .ShouldHaveSingleItem();
        command.ToolCallsPerTurn.ShouldBeNull();
        CronCostProjection.FormatRatio(command.ToolCallsPerTurn).ShouldBe(CronCostProjection.NotMeasured);
    }

    /// <summary>
    /// AC4: computing the ratio never throws, whatever the inputs. Pinned directly because
    /// "absent, not a throw" is a distinct failure mode from "absent, not zero".
    /// </summary>
    [Fact]
    public void Tool_calls_per_turn_never_throws()
    {
        Should.NotThrow(() => CronCostProjection.ToolCallsPerTurn(double.NaN, 0d));
        Should.NotThrow(() => CronCostProjection.ToolCallsPerTurn(double.MaxValue, double.Epsilon));
        CronCostProjection.ToolCallsPerTurn(double.NaN, 10d).ShouldBeNull();
        // An overflowing ratio is not a finite measurement and is reported as absent.
        CronCostProjection.ToolCallsPerTurn(double.MaxValue, double.Epsilon).ShouldBeNull();
    }

    // ── AC5 / AC6: null is not zero ────────────────────────────────────────

    /// <summary>
    /// AC5: an unmeasured total is neither modelled nor rendered as <c>0</c>, and a MEASURED zero
    /// renders differently. Both halves matter - the clause is about distinguishability.
    /// </summary>
    [Fact]
    public void Unmeasured_total_is_not_rendered_or_modelled_as_zero()
    {
        var rows = CronCostProjection.Project(
            [
                Cost("unmeasured", runs: 3, measuredRuns: 0),
                Cost("measured-zero", runs: 3, measuredRuns: 3, tokens: 0, toolCalls: 0, turns: 0)
            ],
            null)
            .ToDictionary(r => r.JobId, StringComparer.Ordinal);

        rows["unmeasured"].Cost.TotalTokens.ShouldBeNull();
        rows["measured-zero"].Cost.TotalTokens.ShouldBe(0);

        CronCostProjection.FormatCount(rows["unmeasured"].Cost.TotalTokens)
            .ShouldBe(CronCostProjection.NotMeasured);
        CronCostProjection.FormatCount(rows["measured-zero"].Cost.TotalTokens).ShouldBe("0");

        CronCostProjection.FormatCount(rows["unmeasured"].Cost.TotalTokens)
            .ShouldNotBe(CronCostProjection.FormatCount(rows["measured-zero"].Cost.TotalTokens));

        // The per-run average follows the same rule and is never divided by RunCount.
        rows["unmeasured"].Cost.AverageTokensPerRun.ShouldBeNull();
        CronCostProjection.FormatAverage(rows["unmeasured"].Cost.AverageTokensPerRun)
            .ShouldBe(CronCostProjection.NotMeasured);
        CronCostProjection.FormatDuration(rows["unmeasured"].Cost.TotalDurationMs)
            .ShouldBe(CronCostProjection.NotMeasured);
    }

    /// <summary>
    /// AC6: a job with runs but no measurements is distinguishable from a genuinely cheap one. Both
    /// counts survive the projection, and the averages divide by MeasuredRunCount rather than
    /// RunCount - the dilution the two counts exist to prevent.
    /// </summary>
    [Fact]
    public void Job_with_runs_but_no_measurements_reads_as_unmeasured_not_zero_cost()
    {
        var rows = CronCostProjection.Project(
            [
                Cost("ran-but-unmeasured", runs: 120, measuredRuns: 0),
                // Same total, but only a QUARTER of the runs measured it.
                Cost("partially-measured", runs: 120, measuredRuns: 30, tokens: 3_000, toolCalls: 300, turns: 150)
            ],
            null)
            .ToDictionary(r => r.JobId, StringComparer.Ordinal);

        var unmeasured = rows["ran-but-unmeasured"];
        unmeasured.Cost.RunCount.ShouldBe(120);
        unmeasured.Cost.MeasuredRunCount.ShouldBe(0);
        unmeasured.Cost.TotalTokens.ShouldBeNull();
        unmeasured.ToolCallsPerTurn.ShouldBeNull();

        var partial = rows["partially-measured"];
        // 3,000 tokens over 30 MEASURED runs = 100. Dividing by RunCount would give 25.
        partial.Cost.AverageTokensPerRun!.Value.ShouldBe(100d, tolerance: 1e-9);
        partial.Cost.AverageTokensPerRun.ShouldNotBe(25d);
        partial.ToolCallsPerTurn!.Value.ShouldBe(2d, tolerance: 1e-9);
    }

    // ── AC7: retention clamp notice ────────────────────────────────────────

    /// <summary>
    /// AC7: the truncation flag is surfaced only when the response actually sets it, and the
    /// effective window comes off the response rather than from the request.
    /// </summary>
    [Fact]
    public void Window_truncation_is_reported_only_when_the_response_sets_the_flag()
    {
        var untruncated = CronCostProjection.Project(
            [Cost("a", 1, 1, tokens: 1, windowDays: 7, truncated: false)], null);
        CronCostProjection.WindowTruncated(untruncated).ShouldBeFalse();
        CronCostProjection.EffectiveWindowDays(untruncated).ShouldBe(7);

        var truncated = CronCostProjection.Project(
            [Cost("a", 1, 1, tokens: 1, windowDays: 3, truncated: true)], null);
        CronCostProjection.WindowTruncated(truncated).ShouldBeTrue();
        CronCostProjection.EffectiveWindowDays(truncated).ShouldBe(3);

        // An empty response asserts nothing about the window.
        CronCostProjection.WindowTruncated([]).ShouldBeFalse();
        CronCostProjection.EffectiveWindowDays([]).ShouldBeNull();
    }

    // ── AC8: navigation keyed on the row's own job id ──────────────────────

    /// <summary>
    /// AC8: the navigation target still matches the row's own job id after a re-sort. Both orderings
    /// are computed from the SAME rows, so a position-derived target would necessarily disagree.
    /// </summary>
    [Fact]
    public void Navigation_target_matches_the_rows_own_job_id_after_a_resort()
    {
        var ranked = CronCostProjection.Project(
            [
                Cost("zulu", 10, 10, tokens: 900),
                Cost("alpha", 10, 10, tokens: 500),
                Cost("mike", 10, 10, tokens: 700)
            ],
            null);

        ranked.Select(r => r.JobId).ShouldBe(["zulu", "mike", "alpha"]);

        var resorted = ranked.OrderBy(r => r.JobId, StringComparer.Ordinal).ToList();
        resorted.Select(r => r.JobId).ShouldNotBe(ranked.Select(r => r.JobId).ToList());

        var byId = ranked.ToDictionary(r => r.JobId, CronCostProjection.NavigationTarget, StringComparer.Ordinal);
        foreach (var row in resorted)
        {
            CronCostProjection.NavigationTarget(row).ShouldContain(row.JobId);
            CronCostProjection.NavigationTarget(row).ShouldBe(byId[row.JobId]);
        }
    }

    // ── general shape ──────────────────────────────────────────────────────

    /// <summary>
    /// A job id present in the rollup but absent from the job list still renders - a rollup can
    /// outlive its job inside the retention window - and falls back to the id as its label.
    /// </summary>
    [Fact]
    public void Job_name_is_resolved_from_the_list_and_falls_back_to_the_id()
    {
        var rows = CronCostProjection
            .Project([Cost("known", 1, 1, tokens: 2), Cost("orphan", 1, 1, tokens: 1)], [Job("known", "Nightly sweep")])
            .ToDictionary(r => r.JobId, StringComparer.Ordinal);

        rows["known"].JobName.ShouldBe("Nightly sweep");
        rows["orphan"].JobName.ShouldBe("orphan");
    }

    /// <summary>A duplicated job id in the response yields exactly one row, first stamped winning.</summary>
    [Fact]
    public void Duplicate_rollups_yield_a_single_row()
    {
        var row = CronCostProjection
            .Project([Cost("dup", 4, 4, tokens: 100), Cost("dup", 999, 999, tokens: 999_999)], null)
            .ShouldHaveSingleItem();

        row.Cost.RunCount.ShouldBe(4);
        row.Cost.TotalTokens.ShouldBe(100);
    }

    /// <summary>Null arguments are rejected rather than silently reading as "nothing costs anything".</summary>
    [Fact]
    public void Null_arguments_are_rejected()
    {
        Should.Throw<ArgumentNullException>(() => CronCostProjection.Project(null!, null));
        Should.Throw<ArgumentNullException>(() => CronCostProjection.NavigationTarget(null!));
        Should.Throw<ArgumentNullException>(() => CronCostProjection.WindowTruncated(null!));
        Should.Throw<ArgumentNullException>(() => CronCostProjection.EffectiveWindowDays(null!));
    }
}
