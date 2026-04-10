using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Cron.Tests.TestInfrastructure;

internal sealed class CronStoreTestContext : IAsyncDisposable
{
    private CronStoreTestContext(string tempDirectory, string dbPath, SqliteCronStore store)
    {
        TempDirectory = tempDirectory;
        DbPath = dbPath;
        Store = store;
    }

    public string TempDirectory { get; }
    public string DbPath { get; }
    public SqliteCronStore Store { get; }

    public static async Task<CronStoreTestContext> CreateAsync()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-cron-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDirectory, "cron.db");
        var store = new SqliteCronStore(dbPath, new FileSystem());
        await store.InitializeAsync();
        return new CronStoreTestContext(tempDirectory, dbPath, store);
    }

    public static CronJob CreateJob(
        string id,
        string agentId = "agent-a",
        string actionType = "agent-prompt",
        bool enabled = true)
    {
        return new CronJob
        {
            Id = id,
            Name = $"Job {id}",
            Schedule = "*/1 * * * *",
            ActionType = actionType,
            AgentId = agentId,
            Message = "Run scheduled task",
            Enabled = enabled,
            CreatedBy = "test-agent",
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object?> { ["source"] = "tests" }
        };
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(TempDirectory))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
        }
    }
}
