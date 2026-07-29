using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2410: runs stamped <c>running</c> that never receive a terminal write (process kill, host
/// crash, OOM, power loss) were previously immune to both completion and retention pruning.
/// These tests pin the observable outcome of the reaper: the run row becomes
/// <see cref="CronRunStatus.Error"/> with the orphan reason, and is subsequently prunable.
/// </summary>
public sealed class CronOrphanedRunReaperTests
{
    [Fact]
    public async Task ReapOrphanedRunsAsync_MarksStaleRunningRunAsErrorWithOrphanReason()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(-48));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var reapedRun = history.ShouldHaveSingleItem();
        reapedRun.Status.ShouldBe(CronRunStatus.Error);
        reapedRun.Error.ShouldNotBeNull();
        reapedRun.Error!.ShouldContain("orphaned");
        reapedRun.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_ReapsFutureDatedStartedAtBeyondBound()
    {
        // A future-dated started_at (clock skew, restored DB, forced run) must also be reaped.
        // A naive (now - startedAt) > bound comparison yields a negative span and silently skips
        // the row forever -- this is the exact blind spot #2410 fixes via Math.Abs.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(48));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var reapedRun = history.ShouldHaveSingleItem();
        reapedRun.Status.ShouldBe(CronRunStatus.Error);
        reapedRun.Error!.ShouldContain("orphaned");
        reapedRun.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_LeavesInFlightRunWithinBoundUntouched()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        _ = await context.Store.RecordRunStartAsync(JobId.From("job-1"));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(0);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Running);
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_LeavesFutureDatedRunWithinBoundUntouched()
    {
        // Small forward skew must not be treated as an orphan: Math.Abs widens the window
        // symmetrically, it does not make every future-dated run reapable.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddMinutes(5));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(0);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Running);
    }

    [Fact]
    public async Task ReapedRun_BecomesPrunableByRetention()
    {
        // The whole point of #2410: a stuck 'running' row is immune to PurgeRunsOlderThanAsync.
        // After reaping it must be a terminal row that retention can delete.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-90));

        // Before reaping: retention cannot touch it.
        (await context.Store.PurgeRunsOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-30))).ShouldBe(0);

        var scheduler = CreateScheduler(context.Store);
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);

        // The reaper stamps completed_at = now, so age the row and prune.
        await SetRunCompletedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-60));
        var purged = await context.Store.PurgeRunsOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-30));

        purged.ShouldBe(1);
        (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_ClearsStuckRunningLastRunStatusOnJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(-48));

        var scheduler = CreateScheduler(context.Store);
        await scheduler.ReapOrphanedRunsAsync();

        var job = await context.Store.GetAsync(JobId.From("job-1"));
        job.ShouldNotBeNull();
        job!.LastRunStatus.ShouldBe(CronRunStatus.Error);
        job.LastRunError.ShouldNotBeNull();
        job.LastRunError!.ShouldContain("orphaned");
    }

    [Fact]
    public async Task ReapOrphanedRunsAsync_DoesNotTouchTerminalRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run.Id, CronRunStatus.Ok);
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-90));

        var scheduler = CreateScheduler(context.Store);

        var reaped = await scheduler.ReapOrphanedRunsAsync();

        reaped.ShouldBe(0);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var only = history.ShouldHaveSingleItem();
        only.Status.ShouldBe(CronRunStatus.Ok);
        only.Error.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessTick_ReapsOrphanedRuns()
    {
        // The reaper must be wired into the periodic scheduler loop, not only startup.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await SetRunStartedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(-48));

        var scheduler = CreateScheduler(context.Store);
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;

        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Error);
    }

    [Fact]
    public async Task ListRunningRunsAsync_ReturnsOnlyNonTerminalRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var terminal = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(terminal.Id, CronRunStatus.Ok);
        var inFlight = await context.Store.RecordRunStartAsync(JobId.From("job-1"));

        var running = await context.Store.ListRunningRunsAsync();

        running.ShouldHaveSingleItem().Id.Value.ShouldBe(inFlight.Id.Value);
    }

    private static CronScheduler CreateScheduler(ICronStore store)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            [],
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(new CronOptions
            {
                Enabled = true,
                TickIntervalSeconds = 1,
                OrphanedRunThresholdSeconds = 3600
            }),
            NullLogger<CronScheduler>.Instance);
    }

    private static async Task SetRunStartedAt(string dbPath, RunId runId, DateTimeOffset value)
        => await ExecuteAsync(dbPath, "UPDATE cron_runs SET started_at = $value WHERE id = $runId", value, runId);

    private static async Task SetRunCompletedAt(string dbPath, RunId runId, DateTimeOffset value)
        => await ExecuteAsync(dbPath, "UPDATE cron_runs SET completed_at = $value WHERE id = $runId", value, runId);

    private static async Task ExecuteAsync(string dbPath, string sql, DateTimeOffset value, RunId runId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value.ToString("O"));
        command.Parameters.AddWithValue("$runId", runId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
