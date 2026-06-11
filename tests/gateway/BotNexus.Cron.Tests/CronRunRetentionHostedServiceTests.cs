using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

public sealed class CronRunRetentionHostedServiceTests
{
    [Fact]
    public async Task RunRetentionOnceAsync_PurgesExpiredRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run.Id, "completed");

        // Backdate the run to 45 days ago
        await BackdateRunCompletedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-45));

        var options = Options.Create(new CronRunRetentionOptions { RetentionDays = 30 });
        var service = new CronRunRetentionHostedService(
            context.Store, options, NullLogger<CronRunRetentionHostedService>.Instance);

        var purged = await service.RunRetentionOnceAsync();

        purged.ShouldBe(1);
    }

    [Fact]
    public async Task RunRetentionOnceAsync_PreservesRecentRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run.Id, "completed");

        var options = Options.Create(new CronRunRetentionOptions { RetentionDays = 30 });
        var service = new CronRunRetentionHostedService(
            context.Store, options, NullLogger<CronRunRetentionHostedService>.Instance);

        var purged = await service.RunRetentionOnceAsync();

        purged.ShouldBe(0);
    }

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var options = new CronRunRetentionOptions();

        options.RetentionDays.ShouldBe(30);
        options.CheckInterval.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task RunRetentionOnceAsync_UsesConfiguredRetentionDays()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run.Id, "completed");

        // Backdate to 10 days ago
        await BackdateRunCompletedAt(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddDays(-10));

        // With 7 day retention, this should be purged
        var options = Options.Create(new CronRunRetentionOptions { RetentionDays = 7 });
        var service = new CronRunRetentionHostedService(
            context.Store, options, NullLogger<CronRunRetentionHostedService>.Instance);

        var purged = await service.RunRetentionOnceAsync();
        purged.ShouldBe(1);
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
