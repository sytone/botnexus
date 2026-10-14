using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Persistence.Seam.Tests.Webhooks;

/// <summary>Real on-disk SQLite stores for webhook seam tests.</summary>
internal sealed class WebhookSeamStoreFixture : IDisposable
{
    public WebhookSeamStoreFixture()
    {
        RegistrationDatabasePath = Path.Combine(
            Path.GetTempPath(), $"botnexus-webhook-registration-seam-{Guid.NewGuid():N}.db");
        RunDatabasePath = Path.Combine(
            Path.GetTempPath(), $"botnexus-webhook-run-seam-{Guid.NewGuid():N}.db");
    }

    public string RegistrationDatabasePath { get; }
    public string RunDatabasePath { get; }

    public SqliteWebhookRegistrationStore CreateRegistrationStore()
        => new(RegistrationDatabasePath, logger: NullLogger<SqliteWebhookRegistrationStore>.Instance);

    public SqliteWebhookRunStore CreateRunStore()
        => new(RunDatabasePath, logger: NullLogger<SqliteWebhookRunStore>.Instance);

    public async Task<WebhookRegistration> SeedRegistrationAsync()
    {
        var registration = new WebhookRegistration
        {
            Id = WebhookId.Create(),
            Label = "original",
            AgentId = AgentId.From("webhook-seam-agent"),
            Secret = "webhook-seam-secret",
            DefaultResponseMode = WebhookResponseMode.Async,
            Enabled = true,
            CreatedAt = new DateTimeOffset(2026, 9, 30, 4, 0, 0, TimeSpan.Zero),
        };

        return await CreateRegistrationStore().CreateAsync(registration);
    }

    public void Dispose()
    {
        DeleteDatabase(RegistrationDatabasePath);
        DeleteDatabase(RunDatabasePath);
    }

    private static void DeleteDatabase(string databasePath)
    {
        SqlitePoolCleanup.ClearPoolFor(databasePath);
        if (!File.Exists(databasePath))
            return;

        try
        {
            File.Delete(databasePath);
        }
        catch (IOException)
        {
            // Cleanup is best effort on Windows; a lingering handle must not mask a seam result.
        }
    }
}
