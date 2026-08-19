using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2641: per-run cost metrics on <c>cron_runs</c> and the per-job rollup derived from them.
/// </summary>
/// <remarks>
/// Every assertion in this file exists to pin one of two things that a plausible-looking
/// implementation gets wrong: that a terminal status OTHER than <c>ok</c> still records cost, and
/// that an unmeasured value stays NULL instead of becoming a zero which would read as "this job is
/// free" and invert the exact ranking the feature exists to produce.
/// </remarks>
public sealed class CronRunCostMetricsTests
{
    // ---------------------------------------------------------------------------------------
    // AC1: cost columns written by the existing finalization path, for EVERY terminal status.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(CronRunStatus.Ok)]
    [InlineData(CronRunStatus.Error)]
    [InlineData(CronRunStatus.TimedOut)]
    [InlineData(CronRunStatus.NoToolCalls)]
    [InlineData(CronRunStatus.DeliveryFailed)]
    [InlineData(CronRunStatus.Aborted)]
    public async Task RecordRunCompleteAsync_RecordsCost_ForEveryTerminalStatus(string status)
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-terminal"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-terminal"));

        await context.Store.RecordRunCompleteAsync(
            run.Id,
            status,
            error: status == CronRunStatus.Ok ? null : "boom",
            cost: new CronRunCost(TurnCount: 4, ToolCallCount: 11, DurationMs: 9_000, PromptTokens: 65_300, CompletionTokens: 1_200));

        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-terminal"));
        var stored = history.Single();
        stored.Status.ShouldBe(status);
        stored.Cost.TurnCount.ShouldBe(4);
        stored.Cost.ToolCallCount.ShouldBe(11);
        stored.Cost.DurationMs.ShouldBe(9_000);
        stored.Cost.PromptTokens.ShouldBe(65_300);
        stored.Cost.CompletionTokens.ShouldBe(1_200);
        stored.Cost.TotalTokens.ShouldBe(66_500);
    }

    /// <summary>
    /// AC1's named clause: a FAILED run still records the cost of the work it did before failing.
    /// A run that errors after 12 turns is not a free run, and recording it as one would make the
    /// most broken jobs on the platform look like the cheapest.
    /// </summary>
    [Fact]
    public async Task RecordRunCompleteAsync_FailedRun_StillRecordsCostOfWorkDoneBeforeFailing()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-failed"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-failed"));

        await context.Store.RecordRunCompleteAsync(
            run.Id,
            CronRunStatus.Error,
            error: "tool exploded",
            cost: new CronRunCost(TurnCount: 12, ToolCallCount: 30, DurationMs: 640_000, PromptTokens: 412_000, CompletionTokens: 8_400));

        var stored = (await context.Store.GetRunHistoryAsync(JobId.From("job-failed"))).Single();
        stored.Status.ShouldBe(CronRunStatus.Error);
        stored.Error.ShouldBe("tool exploded");
        stored.Cost.TurnCount.ShouldBe(12);
        stored.Cost.ToolCallCount.ShouldBe(30);
        stored.Cost.PromptTokens.ShouldBe(412_000);
    }

    /// <summary>
    /// The #3161 alert-delivery amendment re-records the same terminal row with no cost argument.
    /// A plain assignment would wipe the measurement the first write stored; COALESCE preserves it.
    /// </summary>
    [Fact]
    public async Task RecordRunCompleteAsync_SecondWriteWithoutCost_DoesNotEraseRecordedCost()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-amend"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-amend"));

        await context.Store.RecordRunCompleteAsync(
            run.Id, CronRunStatus.Error, "first", cost: new CronRunCost(TurnCount: 3, PromptTokens: 5_000));

        await context.Store.RecordRunCompleteAsync(
            run.Id, CronRunStatus.Error, "first Failure alert could not be delivered: gone", cost: null);

        var stored = (await context.Store.GetRunHistoryAsync(JobId.From("job-amend"))).Single();
        stored.Error.ShouldBe("first Failure alert could not be delivered: gone");
        stored.Cost.TurnCount.ShouldBe(3);
        stored.Cost.PromptTokens.ShouldBe(5_000);
    }

    // ---------------------------------------------------------------------------------------
    // AC2: turn/tool/duration populated without depending on provider token reporting.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordRunCompleteAsync_TurnToolAndDuration_ArePinnedIndependentlyOfTokens()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-no-tokens"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-no-tokens"));

        // The provider reported no usage at all - the state the platform is in until the
        // provider-usage seam lands. The other three must still be measured.
        await context.Store.RecordRunCompleteAsync(
            run.Id,
            CronRunStatus.Ok,
            cost: new CronRunCost(TurnCount: 7, ToolCallCount: 19, DurationMs: 123_456));

        var stored = (await context.Store.GetRunHistoryAsync(JobId.From("job-no-tokens"))).Single();
        stored.Cost.TurnCount.ShouldBe(7);
        stored.Cost.ToolCallCount.ShouldBe(19);
        stored.Cost.DurationMs.ShouldBe(123_456);
        stored.Cost.PromptTokens.ShouldBeNull();
        stored.Cost.CompletionTokens.ShouldBeNull();
        stored.Cost.TotalTokens.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------------------
    // AC3: NULL token count reads as "not measured", distinct from a measured zero.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task NullTokenCount_ReadsAsNotMeasured_NotAsZero()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-null"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-null"));

        await context.Store.RecordRunCompleteAsync(run.Id, CronRunStatus.Ok, cost: new CronRunCost(TurnCount: 1));

        var stored = (await context.Store.GetRunHistoryAsync(JobId.From("job-null"))).Single();
        stored.Cost.PromptTokens.ShouldBeNull();
        stored.Cost.PromptTokens.ShouldNotBe(0);
        stored.Cost.TotalTokens.ShouldBeNull();
    }

    /// <summary>
    /// The counterpart: a genuinely measured zero must survive as zero. If it collapsed to NULL,
    /// "the provider charged us nothing" and "we never looked" would again be indistinguishable -
    /// the same conflation from the other direction.
    /// </summary>
    [Fact]
    public async Task MeasuredZeroTokenCount_IsDistinctFromNull()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-zero"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-zero"));

        await context.Store.RecordRunCompleteAsync(
            run.Id, CronRunStatus.Ok, cost: new CronRunCost(PromptTokens: 0, CompletionTokens: 0));

        var stored = (await context.Store.GetRunHistoryAsync(JobId.From("job-zero"))).Single();
        stored.Cost.PromptTokens.ShouldBe(0);
        stored.Cost.TotalTokens.ShouldBe(0);
        stored.Cost.TotalTokens.ShouldNotBeNull();
    }

    [Fact]
    public async Task RollupDoesNotCountUnmeasuredRunsAsMeasuredZeroes()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-mixed"));

        // Two runs: one measured at 1000 tokens, one never measured at all. The average must be
        // 1000 (over the ONE measured run), not 500 (diluted by an unmeasured run read as zero).
        var measured = await context.Store.RecordRunStartAsync(JobId.From("job-mixed"));
        await context.Store.RecordRunCompleteAsync(
            measured.Id, CronRunStatus.Ok, cost: new CronRunCost(PromptTokens: 1_000));

        var unmeasured = await context.Store.RecordRunStartAsync(JobId.From("job-mixed"));
        await context.Store.RecordRunCompleteAsync(unmeasured.Id, CronRunStatus.Ok);

        var rollup = (await context.Store.GetJobCostRollupsAsync([JobId.From("job-mixed")], 7)).Single();
        rollup.RunCount.ShouldBe(2);
        rollup.MeasuredRunCount.ShouldBe(1);
        rollup.TotalTokens.ShouldBe(1_000);
        rollup.AverageTokensPerRun.ShouldBe(1_000);
    }

    [Fact]
    public async Task RollupOfEntirelyUnmeasuredJob_ReportsNullTotal_NotZero()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-command", actionType: "command"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-command"));
        await context.Store.RecordRunCompleteAsync(run.Id, CronRunStatus.Ok);

        var rollup = (await context.Store.GetJobCostRollupsAsync([JobId.From("job-command")], 7)).Single();
        rollup.RunCount.ShouldBe(1);
        rollup.MeasuredRunCount.ShouldBe(0);
        rollup.TotalTokens.ShouldBeNull();
        rollup.AverageTokensPerRun.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------------------
    // AC4: pre-existing rows and jobs unaffected by the migration.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Pins the migration against a row written by the PRE-#2641 schema: a <c>cron_runs</c> table
    /// with no cost columns at all. The row must still load, and its cost must read as entirely
    /// unmeasured rather than as a run that cost nothing.
    /// </summary>
    [Fact]
    public async Task Migration_PreExistingRunRow_StillLoads_AndReadsAsUnmeasured()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-cron-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "cron.db");

        try
        {
            // Build the OLD schema by hand - cron_runs without any cost column - and insert a row.
            await using (var seed = new SqliteConnection($"Data Source={dbPath}"))
            {
                await seed.OpenAsync();
                await using var create = seed.CreateCommand();
                create.CommandText = """
                    CREATE TABLE cron_jobs (
                        id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        schedule TEXT NOT NULL,
                        action_type TEXT NOT NULL,
                        agent_id TEXT NULL,
                        message TEXT NULL,
                        webhook_url TEXT NULL,
                        shell_command TEXT NULL,
                        enabled INTEGER NOT NULL DEFAULT 1,
                        created_by TEXT NULL,
                        created_at TEXT NOT NULL,
                        last_run_at TEXT NULL,
                        next_run_at TEXT NULL,
                        last_run_status TEXT NULL,
                        last_run_error TEXT NULL,
                        metadata_json TEXT NULL
                    );

                    CREATE TABLE cron_runs (
                        id TEXT PRIMARY KEY,
                        job_id TEXT NOT NULL,
                        started_at TEXT NOT NULL,
                        completed_at TEXT NULL,
                        status TEXT NOT NULL,
                        error TEXT NULL,
                        session_id TEXT NULL
                    );

                    INSERT INTO cron_jobs (id, name, schedule, action_type, agent_id, message, enabled, created_at)
                    VALUES ('legacy-job', 'Legacy', '*/5 * * * *', 'agent-prompt', 'agent-a', 'go', 1, '2026-01-01T00:00:00.0000000+00:00');

                    INSERT INTO cron_runs (id, job_id, started_at, completed_at, status)
                    VALUES ('legacy-run', 'legacy-job', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:01:00.0000000+00:00', 'ok');
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var store = new SqliteCronStore(dbPath, new FileSystem());
            await store.InitializeAsync();

            // AC4 clause 1: the pre-existing run row still loads.
            var runs = await store.GetRunHistoryAsync(JobId.From("legacy-job"));
            var legacy = runs.Single();
            legacy.Id.Value.ShouldBe("legacy-run");
            legacy.Status.ShouldBe(CronRunStatus.Ok);

            // AC4 clause 2 + AC3: it reads as unmeasured, NOT as a zero-cost run.
            legacy.Cost.TurnCount.ShouldBeNull();
            legacy.Cost.ToolCallCount.ShouldBeNull();
            legacy.Cost.DurationMs.ShouldBeNull();
            legacy.Cost.PromptTokens.ShouldBeNull();
            legacy.Cost.TotalTokens.ShouldBeNull();
            legacy.Cost.IsEmpty.ShouldBeTrue();

            // AC4 clause 3: the pre-existing job still loads and still schedules identically.
            var job = await store.GetAsync(JobId.From("legacy-job"));
            job.ShouldNotBeNull();
            job!.Schedule.ShouldBe("*/5 * * * *");
            job.Enabled.ShouldBeTrue();
        }
        finally
        {
            // NOT ClearAllPools(): process-global, disposes sibling tests' live handles (#3324).
            SqlitePoolCleanup.ClearPoolFor(dbPath);
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // AC5: per-job rollup ranks by TOTAL (per-run x frequency), not by per-run cost.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The fixture is the exact inversion present in the live data: <c>daily-enrichment</c> costs
    /// ~65k per run but fires twice; <c>issue-maintenance</c> costs ~17k per run but fires eight
    /// times and is therefore the larger total consumer. A rollup ranked on per-run average would
    /// put them in the opposite order and would be wrong in precisely the way this issue was filed
    /// about.
    /// </summary>
    [Fact]
    public async Task Rollup_RanksByTotal_NotByPerRunCost()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("daily-enrichment"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("issue-maintenance"));

        await RecordRunsAsync(context, "daily-enrichment", runs: 2, promptTokensPerRun: 65_000);
        await RecordRunsAsync(context, "issue-maintenance", runs: 8, promptTokensPerRun: 17_000);

        var rollups = await context.Store.GetJobCostRollupsAsync(
            [JobId.From("daily-enrichment"), JobId.From("issue-maintenance")], 7);

        // Per-run, daily-enrichment is nearly 4x more expensive...
        var enrichment = rollups.Single(r => r.JobId.Value == "daily-enrichment");
        var maintenance = rollups.Single(r => r.JobId.Value == "issue-maintenance");
        enrichment.AverageTokensPerRun.ShouldBe(65_000);
        maintenance.AverageTokensPerRun.ShouldBe(17_000);
        enrichment.AverageTokensPerRun!.Value.ShouldBeGreaterThan(maintenance.AverageTokensPerRun!.Value);

        // ...but issue-maintenance is the larger TOTAL consumer, and that is the ranking.
        enrichment.TotalTokens.ShouldBe(130_000);
        maintenance.TotalTokens.ShouldBe(136_000);
        rollups[0].JobId.Value.ShouldBe("issue-maintenance");
        rollups[1].JobId.Value.ShouldBe("daily-enrichment");
    }

    [Fact]
    public async Task Rollup_ReportsRunsInWindow_AverageAndTotal()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-rollup"));
        await RecordRunsAsync(context, "job-rollup", runs: 4, promptTokensPerRun: 1_000, toolCallsPerRun: 3, turnsPerRun: 2, durationMsPerRun: 500);

        var rollup = (await context.Store.GetJobCostRollupsAsync([JobId.From("job-rollup")], 7)).Single();
        rollup.RunCount.ShouldBe(4);
        rollup.MeasuredRunCount.ShouldBe(4);
        rollup.TotalTokens.ShouldBe(4_000);
        rollup.AverageTokensPerRun.ShouldBe(1_000);
        rollup.TotalToolCalls.ShouldBe(12);
        rollup.TotalTurns.ShouldBe(8);
        rollup.TotalDurationMs.ShouldBe(2_000);
    }

    [Fact]
    public async Task Rollup_EmptyJobScope_ReturnsNothing_NeverEveryJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-scoped"));
        await RecordRunsAsync(context, "job-scoped", runs: 1, promptTokensPerRun: 100);

        var rollups = await context.Store.GetJobCostRollupsAsync([], 7);
        rollups.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // AC6: rollup window reconciled with retention.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Rollup_WindowLongerThanRetention_IsClampedAndReportsTruncation()
    {
        await using var context = await CronStoreTestContext.CreateAsync(retentionDays: 30);
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-window"));
        await RecordRunsAsync(context, "job-window", runs: 1, promptTokensPerRun: 10);

        var rollup = (await context.Store.GetJobCostRollupsAsync([JobId.From("job-window")], windowDays: 90)).Single();

        rollup.WindowDays.ShouldBe(30);
        rollup.WindowTruncatedByRetention.ShouldBeTrue();
    }

    [Fact]
    public async Task Rollup_WindowWithinRetention_IsHonouredAndReportsNoTruncation()
    {
        await using var context = await CronStoreTestContext.CreateAsync(retentionDays: 30);
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-window-ok"));
        await RecordRunsAsync(context, "job-window-ok", runs: 1, promptTokensPerRun: 10);

        var rollup = (await context.Store.GetJobCostRollupsAsync([JobId.From("job-window-ok")], windowDays: 7)).Single();

        rollup.WindowDays.ShouldBe(7);
        rollup.WindowTruncatedByRetention.ShouldBeFalse();
    }

    /// <summary>
    /// A run older than the window contributes nothing, so a window figure is genuinely a window
    /// figure rather than an all-time total wearing a window's label.
    /// </summary>
    [Fact]
    public async Task Rollup_ExcludesRunsOlderThanTheWindow()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-old"));

        var recent = await context.Store.RecordRunStartAsync(JobId.From("job-old"));
        await context.Store.RecordRunCompleteAsync(recent.Id, CronRunStatus.Ok, cost: new CronRunCost(PromptTokens: 500));

        var stale = await context.Store.RecordRunStartAsync(JobId.From("job-old"));
        await context.Store.RecordRunCompleteAsync(stale.Id, CronRunStatus.Ok, cost: new CronRunCost(PromptTokens: 9_999));
        await BackdateRunAsync(context, stale.Id.Value, DateTimeOffset.UtcNow.AddDays(-20));

        var rollup = (await context.Store.GetJobCostRollupsAsync([JobId.From("job-old")], windowDays: 7)).Single();
        rollup.RunCount.ShouldBe(1);
        rollup.TotalTokens.ShouldBe(500);
    }

    private static async Task RecordRunsAsync(
        CronStoreTestContext context,
        string jobId,
        int runs,
        long promptTokensPerRun,
        int toolCallsPerRun = 1,
        int turnsPerRun = 1,
        long durationMsPerRun = 100)
    {
        for (var i = 0; i < runs; i++)
        {
            var run = await context.Store.RecordRunStartAsync(JobId.From(jobId));
            await context.Store.RecordRunCompleteAsync(
                run.Id,
                CronRunStatus.Ok,
                cost: new CronRunCost(
                    TurnCount: turnsPerRun,
                    ToolCallCount: toolCallsPerRun,
                    DurationMs: durationMsPerRun,
                    PromptTokens: promptTokensPerRun));
        }
    }

    private static async Task BackdateRunAsync(CronStoreTestContext context, string runId, DateTimeOffset startedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={context.DbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cron_runs SET started_at = $startedAt WHERE id = $id";
        command.Parameters.AddWithValue("$startedAt", startedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync();
    }
}
