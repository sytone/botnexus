using System.Globalization;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Webhooks.Tests;

/// <summary>
/// Schema-version contract for the shared webhook SQLite database.
/// </summary>
public sealed class SqliteWebhookSchemaVersionTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-webhook-schema-version-").FullName;

    [Fact]
    public async Task Both_webhook_store_initializers_record_the_shared_schema_version()
    {
        var path = Path.Combine(_directory, "webhooks.db");
        var registrations = new SqliteWebhookRegistrationStore(path);
        var runs = new SqliteWebhookRunStore(path);

        await registrations.InitializeAsync();
        await runs.InitializeAsync();

        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using var meta = connection.CreateCommand();
        meta.CommandText = $"SELECT value FROM {SqliteStoreIdentity.TableName} WHERE key = $key;";
        meta.Parameters.AddWithValue("$key", SqliteSchemaVersion.SchemaVersionKey);
        (meta.ExecuteScalar() as string).ShouldBe(
            SqliteWebhookRegistrationStore.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(pragma.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(
            SqliteWebhookRegistrationStore.CurrentSchemaVersion);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_directory);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup: Windows may briefly retain a SQLite sidecar handle.
        }
    }
}
