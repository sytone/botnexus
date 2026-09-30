using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Persistence.Seam.Tests.Cron;

/// <summary>
/// Real on-disk SQLite database for cron seam tests (issue #3327, clause 4).
/// </summary>
/// <remarks>
/// Each store instance opens its own non-pooled connections. A fresh store therefore
/// verifies the committed database state rather than any writer-local state.
/// </remarks>
internal sealed class CronSeamStoreFixture : IDisposable
{
    public CronSeamStoreFixture()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"botnexus-cron-seam-{Guid.NewGuid():N}.db");
    }

    public string DatabasePath { get; }

    /// <summary>Creates an independent real store against the shared database file.</summary>
    public SqliteCronStore CreateStore()
        => new(DatabasePath, logger: NullLogger<SqliteCronStore>.Instance);

    /// <summary>Persists a minimal enabled job for a seam scenario.</summary>
    public async Task<CronJob> SeedAsync(string jobId)
    {
        var job = new CronJob
        {
            Id = JobId.From(jobId),
            Name = "original",
            Schedule = "0 * * * *",
            ActionType = "agent-prompt",
            AgentId = AgentId.From("seam-agent"),
            Message = "seam prompt",
            CreatedBy = "seam-test",
            Enabled = true,
        };

        return await CreateStore().CreateAsync(job);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(DatabasePath);
        if (!File.Exists(DatabasePath))
            return;

        try
        {
            File.Delete(DatabasePath);
        }
        catch (IOException)
        {
            // Cleanup is best effort on Windows; a lingering handle must not mask a seam result.
        }
    }
}
