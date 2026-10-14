using System.Globalization;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace BotNexus.Cron.Tests;

public sealed class CronRunActivityTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetRunActivityAsync_Empty_DefaultUtcWindowAndNullTotals()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        ICronStore store = Store(context);
        var result = await store.GetRunActivityAsync();
        result.RequestedStartInclusiveUtc.ShouldBe(Now.AddHours(-24));
        result.RequestedEndExclusiveUtc.ShouldBe(Now);
        result.EffectiveStartInclusiveUtc.ShouldBe(Now.AddHours(-24));
        result.EffectiveEndExclusiveUtc.ShouldBe(Now);
        result.WindowTruncatedByRetention.ShouldBeFalse();
        result.Totals.RunCount.ShouldBe(0);
        result.Totals.JobCount.ShouldBe(0);
        result.Totals.TotalTokens.ShouldBeNull();
        result.Totals.TotalPromptTokens.ShouldBeNull();
        result.Totals.TotalCompletionTokens.ShouldBeNull();
        result.Totals.TotalTurns.ShouldBeNull();
        result.Totals.TotalToolCalls.ShouldBeNull();
        result.Totals.TotalDurationMs.ShouldBeNull();
        result.TopJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetRunActivityAsync_NullPartialAndZeroMeasurements_PreservesCoverageAndUnfinalizedCounts()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await Seed(context, "a", Now.AddHours(-1));
        var store = Store(context);
        var unknown = (await store.GetRunActivityAsync()).Totals;
        unknown.RunCount.ShouldBe(1);
        unknown.UnmeasuredRunCount.ShouldBe(1);
        unknown.TotalTokens.ShouldBeNull();
        await Seed(context, "a", Now.AddHours(-2), prompt: 10, turns: 2, tools: 0, duration: 100);
        await Seed(context, "b", Now.AddHours(-3), completion: 7, status: "error", completed: true);
        await Seed(context, "b", Now.AddHours(-4), prompt: 0, completion: 0, status: "error");
        var result = await store.GetRunActivityAsync();
        var totals = result.Totals;
        totals.RunCount.ShouldBe(4);
        totals.JobCount.ShouldBe(2);
        totals.MeasuredRunCount.ShouldBe(3);
        totals.UnmeasuredRunCount.ShouldBe(1);
        totals.RunningRunCount.ShouldBe(2);
        totals.UnfinalizedRunCount.ShouldBe(3);
        totals.TotalPromptTokens.ShouldBe(10);
        totals.TotalCompletionTokens.ShouldBe(7);
        totals.TotalTokens.ShouldBe(17);
        totals.TotalTurns.ShouldBe(2);
        totals.TotalToolCalls.ShouldBe(0);
        totals.TotalDurationMs.ShouldBe(100);
        totals.PromptTokenRunCount.ShouldBe(2);
        totals.CompletionTokenRunCount.ShouldBe(2);
        totals.TurnRunCount.ShouldBe(1);
        totals.ToolCallRunCount.ShouldBe(1);
        totals.DurationRunCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetRunActivityAsync_TopN_TotalsIncludeAllJobsAndTiesAreOrdinal()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        foreach (var id in new[] { "c", "b", "a" })
            await Seed(context, id, Now.AddHours(-1), prompt: 10);
        await Seed(context, "unknown", Now.AddHours(-1));
        var result = await Store(context).GetRunActivityAsync(new CronRunActivityQuery { TopJobLimit = 2 });
        result.Totals.RunCount.ShouldBe(4);
        result.Totals.JobCount.ShouldBe(4);
        result.Totals.TotalTokens.ShouldBe(30);
        result.TopJobs.Select(x => x.JobId.Value).ShouldBe(new[] { "a", "b" });
        result.TopJobs[0].Totals.TotalTokens.ShouldBe(10);
        (await Store(context).GetRunActivityAsync(new CronRunActivityQuery { TopJobLimit = 0 })).TopJobs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetRunActivityAsync_OffsetBounds_AreUtcInclusiveStartExclusiveEnd()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var start = Now.AddHours(-2);
        var end = Now.AddHours(-1);
        await Seed(context, "a", start.AddTicks(-1));
        await Seed(context, "a", start, prompt: 1);
        await Seed(context, "a", end.AddTicks(-1), prompt: 2);
        await Seed(context, "a", end, prompt: 100);
        var result = await Store(context).GetRunActivityAsync(new CronRunActivityQuery
        {
            StartInclusive = start.ToOffset(TimeSpan.FromHours(5.5)),
            EndExclusive = end.ToOffset(TimeSpan.FromHours(-7))
        });
        result.Totals.RunCount.ShouldBe(2);
        result.Totals.TotalTokens.ShouldBe(3);
        result.RequestedStartInclusiveUtc.Offset.ShouldBe(TimeSpan.Zero);
        result.RequestedEndExclusiveUtc.Offset.ShouldBe(TimeSpan.Zero);
        result.EffectiveStartInclusiveUtc.ShouldBe(start);
        result.EffectiveEndExclusiveUtc.ShouldBe(end);
    }

    [Fact]
    public async Task GetRunActivityAsync_RetentionClamp_IsConservativeAndReportedWhenEmpty()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await Seed(context, "old-running", Now.AddDays(-10));
        var store = Store(context, 2);
        var result = await store.GetRunActivityAsync(new CronRunActivityQuery { StartInclusive = Now.AddDays(-10) });
        result.EffectiveStartInclusiveUtc.ShouldBe(Now.AddDays(-2));
        result.WindowTruncatedByRetention.ShouldBeTrue();
        result.Totals.RunCount.ShouldBe(0);
        (await context.Store.GetRunHistoryAsync(JobId.From("old-running"))).ShouldHaveSingleItem();
        var entirelyOld = await store.GetRunActivityAsync(new CronRunActivityQuery
        {
            StartInclusive = Now.AddDays(-10), EndExclusive = Now.AddDays(-9)
        });
        entirelyOld.EffectiveStartInclusiveUtc.ShouldBe(Now.AddDays(-2));
        entirelyOld.EffectiveEndExclusiveUtc.ShouldBe(Now.AddDays(-2));
        entirelyOld.WindowTruncatedByRetention.ShouldBeTrue();
        entirelyOld.TopJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetRunActivityAsync_FutureAndReversedBounds_ClampsOrRejects()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var store = Store(context);
        var future = await store.GetRunActivityAsync(new CronRunActivityQuery
        {
            StartInclusive = Now.AddDays(1), EndExclusive = Now.AddDays(2)
        });
        future.EffectiveStartInclusiveUtc.ShouldBe(Now);
        future.EffectiveEndExclusiveUtc.ShouldBe(Now);
        future.WindowTruncatedByNow.ShouldBeTrue();
        await Should.ThrowAsync<ArgumentException>(() => store.GetRunActivityAsync(new CronRunActivityQuery
        {
            StartInclusive = Now, EndExclusive = Now.AddTicks(-1)
        }));
        await Should.ThrowAsync<ArgumentException>(() => store.GetRunActivityAsync(new CronRunActivityQuery
        {
            StartInclusive = Now, EndExclusive = Now
        }));
    }

    [Fact]
    public async Task GetRunActivityAsync_ExtremeDatesAndRetention_DoNotOverflow()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var result = await Store(context, int.MaxValue).GetRunActivityAsync(new CronRunActivityQuery
        {
            StartInclusive = DateTimeOffset.MinValue, EndExclusive = DateTimeOffset.MaxValue
        });
        result.EffectiveStartInclusiveUtc.ShouldBe(DateTimeOffset.MinValue);
        result.EffectiveEndExclusiveUtc.ShouldBe(Now);
        var early = new SqliteCronStore(context.DbPath, timeProvider: new FixedClock(DateTimeOffset.MinValue));
        var defaultWindow = await early.GetRunActivityAsync();
        defaultWindow.RequestedStartInclusiveUtc.ShouldBe(DateTimeOffset.MinValue);
        defaultWindow.Totals.RunCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetRunActivityAsync_CancelledBeforeInitialization_DoesNotCreateDatabase()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var path = Path.Combine(context.TempDirectory, "cancelled.db");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => new SqliteCronStore(path).GetRunActivityAsync(ct: cancellation.Token));
        File.Exists(path).ShouldBeFalse();
        await Should.ThrowAsync<OperationCanceledException>(() => Store(context).GetRunActivityAsync(ct: cancellation.Token));
    }

    [Fact]
    public async Task GetRunActivityAsync_JobScope_IsExplicitAndDoesNotChangeLegacyEmptyScope()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await Seed(context, "a", Now.AddHours(-1), prompt: 10);
        await Seed(context, "b", Now.AddHours(-1), prompt: 20);
        var result = await Store(context).GetRunActivityAsync(new CronRunActivityQuery { JobId = JobId.From("a") });
        result.Totals.RunCount.ShouldBe(1);
        result.Totals.TotalTokens.ShouldBe(10);
        result.TopJobs.ShouldHaveSingleItem().JobId.ShouldBe(JobId.From("a"));
        (await context.Store.GetJobCostRollupsAsync(Array.Empty<JobId>(), 1)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetRunActivityAsync_TenThousandRows_BoundedTopNAndQueryPlans()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await using var connection = new SqliteConnection($"Data Source={context.DbPath}");
        await connection.OpenAsync();
        for (var i = 0; i < 100; i++)
            await context.Store.CreateAsync(CronStoreTestContext.CreateJob($"job-{i}"));
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys";
            Convert.ToInt64(await foreignKeys.ExecuteScalarAsync(), CultureInfo.InvariantCulture).ShouldBe(1);
        }
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000)
                INSERT INTO cron_runs(id,job_id,started_at,status,prompt_tokens)
                SELECT 'run-'||x, 'job-'||(x%100), $start, 'running', x FROM n
                """;
            seed.Parameters.AddWithValue("$start", Now.AddHours(-1).ToString("O", CultureInfo.InvariantCulture));
            await seed.ExecuteNonQueryAsync();
        }
        var result = await Store(context).GetRunActivityAsync(new CronRunActivityQuery { TopJobLimit = int.MaxValue });
        result.TopJobs.Count.ShouldBe(50);
        result.Totals.RunCount.ShouldBe(10000);
        result.Totals.JobCount.ShouldBe(100);
        result.Totals.TotalTokens.ShouldBe(50005000);
        // Explain the ACTUAL production SQL, including aggregates, grouping, ordering and LIMIT.
        var global = await Plan(connection, scoped: false, top: false);
        var top = await Plan(connection, scoped: false, top: true);
        var scoped = await Plan(connection, scoped: true, top: false);
        var scopedTop = await Plan(connection, scoped: true, top: true);
        global.ShouldContain("idx_cron_runs_started_at");
        top.ShouldContain("idx_cron_runs_started_at");
        scoped.ShouldContain("idx_cron_runs_job_id_started_at");
        scopedTop.ShouldContain("idx_cron_runs_job_id_started_at");
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP INDEX idx_cron_runs_started_at";
            await drop.ExecuteNonQueryAsync();
        }
        var before = await Plan(connection, scoped: false, top: false);
        before.ShouldContain("SCAN cron_runs");
        output.WriteLine($"GLOBAL BEFORE: {before}\nGLOBAL AFTER: {global}\nTOP AFTER: {top}\nJOB TOTALS: {scoped}\nJOB TOP: {scopedTop}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRunActivityAsync_CrossRowPromptOverflow_ThrowsSanitizedException(bool scoped)
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await Seed(context, "RAW-JOB-SENTINEL", Now.AddHours(-1), prompt: long.MaxValue);
        await Seed(context, "RAW-JOB-SENTINEL", Now.AddHours(-2), prompt: 1);
        await AssertOutOfRange(context, scoped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRunActivityAsync_SingleRowCombinedTokenOverflow_ThrowsSanitizedException(bool scoped)
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await Seed(context, "RAW-JOB-SENTINEL", Now.AddHours(-1), prompt: long.MaxValue, completion: 1);
        await AssertOutOfRange(context, scoped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRunActivityAsync_CrossRowDurationOverflow_ThrowsSanitizedException(bool scoped)
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await Seed(context, "RAW-JOB-SENTINEL", Now.AddHours(-1), duration: long.MaxValue);
        await Seed(context, "RAW-JOB-SENTINEL", Now.AddHours(-2), duration: 1);
        await AssertOutOfRange(context, scoped);
    }

    [Fact]
    public async Task GetRunActivityAsync_CrossJobCombinedTokenOverflow_TotalsFailEvenWithTopOne()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await Seed(context, "a", Now.AddHours(-1), prompt: long.MaxValue);
        await Seed(context, "b", Now.AddHours(-1), completion: 1);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Store(context).GetRunActivityAsync(
            new CronRunActivityQuery { TopJobLimit = 1 }));
        error.Message.ShouldBe("Activity measurement is out of range.");
        error.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRunActivityAsync_ExactInt64Boundary_PreservesIntegerSums(bool scoped)
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await Seed(context, "a", Now.AddHours(-1), prompt: long.MaxValue - 1, completion: 1,
            turns: long.MaxValue, tools: long.MaxValue, duration: long.MaxValue - 1);
        await Seed(context, "a", Now.AddHours(-2), duration: 1);
        var result = await Store(context).GetRunActivityAsync(new CronRunActivityQuery
        {
            JobId = scoped ? JobId.From("a") : null
        });
        foreach (var totals in new[] { result.Totals, result.TopJobs.ShouldHaveSingleItem().Totals })
        {
            totals.TotalPromptTokens.ShouldBe(long.MaxValue - 1);
            totals.TotalCompletionTokens.ShouldBe(1);
            totals.TotalTokens.ShouldBe(long.MaxValue);
            totals.TotalTurns.ShouldBe(long.MaxValue);
            totals.TotalToolCalls.ShouldBe(long.MaxValue);
            totals.TotalDurationMs.ShouldBe(long.MaxValue);
        }
    }

    private static async Task AssertOutOfRange(CronStoreTestContext context, bool scoped)
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Store(context).GetRunActivityAsync(
            new CronRunActivityQuery { JobId = scoped ? JobId.From("RAW-JOB-SENTINEL") : null }));
        error.Message.ShouldBe("Activity measurement is out of range.");
        error.InnerException.ShouldBeNull();
        error.ToString().ShouldNotContain("RAW-JOB-SENTINEL");
        error.ToString().ShouldNotContain("SQLite Error");
        error.ToString().ShouldNotContain(context.DbPath);
    }

    private async Task<string> Plan(SqliteConnection connection, bool scoped, bool top)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteCronStore.BuildActivitySql(scoped, top);
        command.Parameters.AddWithValue("$start", Now.AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", Now.ToString("O", CultureInfo.InvariantCulture));
        if (scoped) command.Parameters.AddWithValue("$job", "job-1");
        if (top) command.Parameters.AddWithValue("$limit", 50);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(3));
        var plan = string.Join("; ", lines);
        output.WriteLine(plan);
        return plan;
    }

    private static SqliteCronStore Store(CronStoreTestContext context, int retention = 30)
        => new(context.DbPath, retentionDaysAccessor: () => retention, timeProvider: new FixedClock(Now));

    private static async Task Seed(CronStoreTestContext context, string job, DateTimeOffset started,
        long? prompt = null, long? completion = null, long? turns = null, long? tools = null,
        long? duration = null, string status = "running", bool completed = false)
    {
        if (await context.Store.GetAsync(JobId.From(job)) is null)
            await context.Store.CreateAsync(CronStoreTestContext.CreateJob(job));
        await using var connection = new SqliteConnection($"Data Source={context.DbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cron_runs(id,job_id,started_at,completed_at,status,prompt_tokens,completion_tokens,turn_count,tool_call_count,duration_ms)
            VALUES($id,$job,$start,$completed,$status,$prompt,$completion,$turns,$tools,$duration)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$job", job);
        command.Parameters.AddWithValue("$start", started.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$completed", completed ? Now.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$prompt", (object?)prompt ?? DBNull.Value);
        command.Parameters.AddWithValue("$completion", (object?)completion ?? DBNull.Value);
        command.Parameters.AddWithValue("$turns", (object?)turns ?? DBNull.Value);
        command.Parameters.AddWithValue("$tools", (object?)tools ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)duration ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
