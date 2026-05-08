using BotNexus.Cron.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cron.Tests;

public sealed class SqliteCronStoreTests
{
    [Fact]
    public async Task InitializeAsync_CreatesSchema()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        await context.Store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={context.DbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
            """;

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        tables.ShouldContain("cron_jobs");
        tables.ShouldContain("cron_runs");
    }

    [Fact]
    public async Task CreateAsync_StoresAndRetrievesByid()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1");

        await context.Store.CreateAsync(job);
        var loaded = await context.Store.GetAsync("job-1");

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe("job-1");
        loaded.Name.ShouldBe(job.Name);
        loaded.AgentId.ShouldBe("agent-a");
    }

    [Fact]
    public async Task ListAsync_ReturnsAllJobs()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", "agent-a"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2", "agent-b"));

        var jobs = await context.Store.ListAsync();

        jobs.Count().ShouldBe(2);
        jobs.Select(job => job.Id).OrderBy(id => id).ShouldBe(["job-1", "job-2"]);
    }

    [Fact]
    public async Task ListAsync_FiltersByAgentId()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", "agent-a"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2", "agent-b"));

        var filtered = await context.Store.ListAsync("agent-a");

        filtered.ShouldHaveSingleItem();
        filtered[0].Id.ShouldBe("job-1");
    }

    [Fact]
    public async Task UpdateAsync_ModifiesJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var updated = CronStoreTestContext.CreateJob("job-1") with
        {
            Name = "Updated Name",
            Enabled = false,
            LastRunStatus = "ok"
        };
        await context.Store.UpdateAsync(updated);

        var loaded = await context.Store.GetAsync("job-1");
        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Updated Name");
        loaded.Enabled.ShouldBeFalse();
        loaded.LastRunStatus.ShouldBe("ok");
    }

    [Fact]
    public async Task DeleteAsync_RemovesJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        await context.Store.DeleteAsync("job-1");

        (await context.Store.GetAsync("job-1")).ShouldBeNull();
    }

    [Fact]
    public async Task RecordRunStartAsync_CreatesRunEntry()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var run = await context.Store.RecordRunStartAsync("job-1");

        run.JobId.ShouldBe("job-1");
        run.Status.ShouldBe("running");
        var history = await context.Store.GetRunHistoryAsync("job-1");
        var entry = history.ShouldHaveSingleItem();
        entry.Id.ShouldBe(run.Id);
        entry.Status.ShouldBe("running");
    }

    [Fact]
    public async Task RecordRunCompleteAsync_UpdatesRunStatus()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync("job-1");

        await context.Store.RecordRunCompleteAsync(run.Id, "ok", sessionId: "session-1");
        var history = await context.Store.GetRunHistoryAsync("job-1");

        history.ShouldHaveSingleItem();
        history[0].Status.ShouldBe("ok");
        history[0].SessionId.ShouldBe("session-1");
        history[0].CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetRunHistoryAsync_ReturnsRunsForJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2"));

        var run1 = await context.Store.RecordRunStartAsync("job-1");
        await context.Store.RecordRunCompleteAsync(run1.Id, "ok");
        var run2 = await context.Store.RecordRunStartAsync("job-2");
        await context.Store.RecordRunCompleteAsync(run2.Id, "error", "boom");

        var history = await context.Store.GetRunHistoryAsync("job-1");

        history.ShouldHaveSingleItem();
        history[0].JobId.ShouldBe("job-1");
        history[0].Status.ShouldBe("ok");
    }
}
