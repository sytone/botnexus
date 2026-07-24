using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
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
        var loaded = await context.Store.GetAsync(JobId.From("job-1"));

        loaded.ShouldNotBeNull();
        loaded!.Id.Value.ShouldBe("job-1");
        loaded.Name.ShouldBe(job.Name);
        loaded.AgentId.ShouldBe(AgentId.From("agent-a"));
    }

    [Fact]
    public async Task ListAsync_ReturnsAllJobs()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", "agent-a"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2", "agent-b"));

        var jobs = await context.Store.ListAsync();

        jobs.Count().ShouldBe(2);
        jobs.Select(job => job.Id.Value).OrderBy(id => id).ShouldBe(["job-1", "job-2"]);
    }

    [Fact]
    public async Task ListAsync_FiltersByAgentId()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", "agent-a"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2", "agent-b"));

        var filtered = await context.Store.ListAsync(AgentId.From("agent-a"));

        filtered.ShouldHaveSingleItem();
        filtered[0].Id.Value.ShouldBe("job-1");
    }

    [Fact]
    public async Task UpdateDefinitionAsync_ModifiesDefinitionColumns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var updated = CronStoreTestContext.CreateJob("job-1") with
        {
            Name = "Updated Name",
            Enabled = false
        };
        var saved = await context.Store.UpdateDefinitionAsync(updated);

        saved.ShouldNotBeNull();
        var loaded = await context.Store.GetAsync(JobId.From("job-1"));
        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Updated Name");
        loaded.Enabled.ShouldBeFalse();
    }

    // #2133: a definition update must NOT touch scheduler-owned runtime bookkeeping
    // (LastRun*/NextRunAt) or the CAS-established ConversationId, even if the passed record
    // carries stale values in those fields. This is the store-level guarantee behind the
    // controller/tool acceptance criteria.
    [Fact]
    public async Task UpdateDefinitionAsync_DoesNotRegress_RuntimeOrConversationState()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        // Establish scheduler-owned runtime state and a pinned conversation.
        var runAt = DateTimeOffset.UtcNow;
        await context.Store.RecordRunFinalizationAsync(JobId.From("job-1"), runAt, "ok", null);
        await context.Store.SetNextRunAtAsync(JobId.From("job-1"), runAt.AddMinutes(5));
        var pinned = await context.Store.TrySetConversationIdAsync(
            JobId.From("job-1"), ConversationId.From("conv-winner"));
        pinned!.Value.Value.ShouldBe("conv-winner");

        // A racing definition update carrying stale/empty runtime + conversation fields.
        var stale = CronStoreTestContext.CreateJob("job-1") with
        {
            Name = "Renamed",
            Enabled = false,
            LastRunStatus = "regressed",
            LastRunError = "regressed",
            LastRunAt = null,
            NextRunAt = null,
            ConversationId = null
        };
        await context.Store.UpdateDefinitionAsync(stale);

        var loaded = await context.Store.GetAsync(JobId.From("job-1"));
        loaded.ShouldNotBeNull();
        // Definition columns applied...
        loaded!.Name.ShouldBe("Renamed");
        loaded.Enabled.ShouldBeFalse();
        // ...but runtime bookkeeping and the conversation pin are untouched.
        loaded.LastRunStatus.ShouldBe("ok");
        loaded.LastRunAt.ShouldNotBeNull();
        loaded.NextRunAt.ShouldNotBeNull();
        loaded.ConversationId!.Value.Value.ShouldBe("conv-winner");
    }

    // #2133: scheduler run finalization must not overwrite a concurrent definition edit.
    // The narrow last_run_* write only touches bookkeeping columns.
    [Fact]
    public async Task RecordRunFinalizationAsync_DoesNotClobber_DefinitionColumns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1") with { Schedule = "*/5 * * * *" });

        // Concurrent controller/tool definition edit.
        await context.Store.UpdateDefinitionAsync(
            CronStoreTestContext.CreateJob("job-1") with { Name = "Edited", Schedule = "0 0 * * *", Enabled = false });

        // Scheduler finalizes a run that started before the edit.
        var runAt = DateTimeOffset.UtcNow;
        await context.Store.RecordRunFinalizationAsync(JobId.From("job-1"), runAt, "ok", null);

        var loaded = await context.Store.GetAsync(JobId.From("job-1"));
        loaded.ShouldNotBeNull();
        // Definition edit survives finalization...
        loaded!.Name.ShouldBe("Edited");
        loaded.Schedule.ShouldBe("0 0 * * *");
        loaded.Enabled.ShouldBeFalse();
        // ...and the run bookkeeping is recorded.
        loaded.LastRunStatus.ShouldBe("ok");
    }

    [Fact]
    public async Task CreateAsync_PersistsModelField()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1") with
        {
            Model = "openai/gpt-4.1"
        };

        await context.Store.CreateAsync(job);
        var loaded = await context.Store.GetAsync(JobId.From("job-1"));

        loaded.ShouldNotBeNull();
        loaded!.Model.ShouldBe("openai/gpt-4.1");
    }

    [Fact]
    public async Task CreateAsync_PersistsTemplateFields()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1") with
        {
            TemplateName = "daily-status",
            TemplateParameters = new Dictionary<string, string?>
            {
                ["project"] = "botnexus",
                ["owner"] = "hermes"
            }
        };

        await context.Store.CreateAsync(job);
        var loaded = await context.Store.GetAsync(JobId.From("job-1"));

        loaded.ShouldNotBeNull();
        loaded!.TemplateName.ShouldBe("daily-status");
        loaded.TemplateParameters.ShouldNotBeNull();
        loaded.TemplateParameters!["project"].ShouldBe("botnexus");
        loaded.TemplateParameters["owner"].ShouldBe("hermes");
    }

    [Fact]
    public async Task ListAsync_WithCorruptMetadataJsonOnOneJob_LoadsOtherJobs_AndCorruptJobHasNullMetadata()
    {
        // Regression (#1751): a single corrupted metadata_json value must not poison the
        // whole scan. The bare JsonSerializer.Deserialize used to throw JsonException and
        // abort the entire ListAsync enumeration, so the scheduler could not read ANY jobs.
        // The guard now catches the parse failure, logs a warning with the job id, and lets
        // the job load with null Metadata so the remaining valid jobs still enumerate.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-good", "agent-a"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-bad", "agent-b"));

        // Corrupt the metadata_json of exactly one row directly in SQLite.
        await using (var connection = new SqliteConnection($"Data Source={context.DbPath}"))
        {
            await connection.OpenAsync();
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE cron_jobs SET metadata_json = $bad WHERE id = 'job-bad';";
            corrupt.Parameters.AddWithValue("$bad", "{ this is not valid json ");
            (await corrupt.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        SqliteConnection.ClearAllPools();

        // Must NOT throw: the corrupt row is skipped-to-safe (null metadata), not fatal.
        var jobs = await context.Store.ListAsync();

        jobs.Select(job => job.Id.Value).OrderBy(id => id).ShouldBe(["job-bad", "job-good"]);
        var good = jobs.Single(job => job.Id.Value == "job-good");
        good.Metadata.ShouldNotBeNull();
        good.Metadata!.ShouldContainKey("source");
        good.Metadata!["source"]!.ToString().ShouldBe("tests");
        var bad = jobs.Single(job => job.Id.Value == "job-bad");
        bad.Metadata.ShouldBeNull("A corrupt metadata_json must degrade to null metadata, not abort the load.");
    }

    [Fact]
    public async Task GetAsync_WithCorruptTemplateParametersJson_LoadsJobWithNullTemplateParameters()
    {
        // Regression (#1751): a corrupt template_parameters_json must not throw out of the
        // reader. The job still loads; TemplateParameters degrades to null.
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-tpl", "agent-a"));

        await using (var connection = new SqliteConnection($"Data Source={context.DbPath}"))
        {
            await connection.OpenAsync();
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE cron_jobs SET template_parameters_json = $bad WHERE id = 'job-tpl';";
            corrupt.Parameters.AddWithValue("$bad", "[not, valid");
            (await corrupt.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        SqliteConnection.ClearAllPools();

        var loaded = await context.Store.GetAsync(JobId.From("job-tpl"));

        loaded.ShouldNotBeNull();
        loaded!.Id.Value.ShouldBe("job-tpl");
        loaded.TemplateParameters.ShouldBeNull("A corrupt template_parameters_json must degrade to null, not abort the load.");
    }

    [Fact]
    public async Task DeleteAsync_RemovesJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        await context.Store.DeleteAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task RecordRunStartAsync_CreatesRunEntry()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));

        run.JobId.Value.ShouldBe("job-1");
        run.Status.ShouldBe("running");
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var entry = history.ShouldHaveSingleItem();
        entry.Id.ShouldBe(run.Id);
        entry.Status.ShouldBe("running");
    }

    [Fact]
    public async Task RecordRunCompleteAsync_UpdatesRunStatus()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-1"));

        await context.Store.RecordRunCompleteAsync(run.Id, "ok", sessionId: SessionId.From("session-1"));
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));

        history.ShouldHaveSingleItem();
        history[0].Status.ShouldBe("ok");
        history[0].SessionId!.Value.Value.ShouldBe("session-1");
        history[0].CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetRunHistoryAsync_ReturnsRunsForJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2"));

        var run1 = await context.Store.RecordRunStartAsync(JobId.From("job-1"));
        await context.Store.RecordRunCompleteAsync(run1.Id, "ok");
        var run2 = await context.Store.RecordRunStartAsync(JobId.From("job-2"));
        await context.Store.RecordRunCompleteAsync(run2.Id, "error", "boom");

        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));

        history.ShouldHaveSingleItem();
        history[0].JobId.Value.ShouldBe("job-1");
        history[0].Status.ShouldBe("ok");
    }
}
