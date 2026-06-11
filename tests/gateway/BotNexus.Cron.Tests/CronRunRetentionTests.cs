using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Tests;

public sealed class CronRunRetentionTests
{
    [Fact]
    public async Task PurgeRunsOlderThanAsync_DeletesCompletedRunsOlderThanCutoff()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1");
        await context.Store.CreateAsync(job);

        // Record a run that completed 60 days ago
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run.Id, "completed");

        // Manually backdate the completed_at to 60 days ago
        await BackdateRunCompletedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-60));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await context.Store.PurgeRunsOlderThanAsync(cutoff);

        purged.ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldBeEmpty();
    }

    [Fact]
    public async Task PurgeRunsOlderThanAsync_PreservesRecentCompletedRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1");
        await context.Store.CreateAsync(job);

        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run.Id, "completed");

        // Cutoff is 30 days ago -- run completed just now, should be preserved
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await context.Store.PurgeRunsOlderThanAsync(cutoff);

        purged.ShouldBe(0);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PurgeRunsOlderThanAsync_DeletesFailedRunsOlderThanCutoff()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1");
        await context.Store.CreateAsync(job);

        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run.Id, "failed", "timeout");

        await BackdateRunCompletedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-45));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await context.Store.PurgeRunsOlderThanAsync(cutoff);

        purged.ShouldBe(1);
    }

    [Fact]
    public async Task PurgeRunsOlderThanAsync_NeverDeletesRunningRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1");
        await context.Store.CreateAsync(job);

        // Start a run but don't complete it -- stays in "running" status
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));

        // Try to purge with very aggressive cutoff (1 day ago)
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var purged = await context.Store.PurgeRunsOlderThanAsync(cutoff);

        purged.ShouldBe(0);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.Count.ShouldBe(1);
        history[0].Status.ShouldBe("running");
    }

    [Fact]
    public async Task PurgeRunsOlderThanAsync_ReturnsZeroWhenNothingToPurge()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1");
        await context.Store.CreateAsync(job);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await context.Store.PurgeRunsOlderThanAsync(cutoff);

        purged.ShouldBe(0);
    }

    [Fact]
    public async Task PurgeRunsOlderThanAsync_HandlesMultipleJobsCorrectly()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2"));

        var run1 = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run1.Id, "completed");
        await BackdateRunCompletedAt(context.DbPath, run1.Id, DateTimeOffset.UtcNow.AddDays(-40));

        var run2 = await context.Store.RecordRunStartAsync(JobId.From("job-2"));
        await context.Store.RecordRunCompleteAsync(run2.Id, "completed");
        await BackdateRunCompletedAt(context.DbPath, run2.Id, DateTimeOffset.UtcNow.AddDays(-40));

        // Recent run that should be preserved
        var run3 = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run3.Id, "completed");

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await context.Store.PurgeRunsOlderThanAsync(cutoff);

        purged.ShouldBe(2);
        var history1 = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history1.Count.ShouldBe(1); // Only the recent one
        var history2 = await context.Store.GetRunHistoryAsync(JobId.From("job-2"));
        history2.ShouldBeEmpty();
    }

    private static async Task BackdateRunCompletedAt(string dbPath, RunId runId, DateTimeOffset backdatedTime)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cron_runs SET completed_at = $completedAt WHERE id = $runId";
        command.Parameters.AddWithValue("$completedAt", backdatedTime.ToString("O"));
        command.Parameters.AddWithValue("$runId", runId.Value);
        await command.ExecuteNonQueryAsync();
    }
}
