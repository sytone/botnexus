using System.Globalization;
using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Persistence.Seam.Tests;

/// <summary>
/// Store-level contract for the four direct SQLite stores adopting schema versioning in #2835.
/// </summary>
public sealed class SqliteStoreSchemaVersionContractTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-store-schema-version-").FullName;

    [Fact]
    public async Task Cron_store_records_schema_version_in_both_slots()
    {
        var path = DatabasePath("cron.db");
        var store = new SqliteCronStore(path);

        await store.InitializeAsync();

        AssertVersion(path, SqliteCronStore.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Session_store_records_schema_version_in_both_slots()
    {
        var path = DatabasePath("sessions.db");
        var conversations = new InMemoryConversationStore();
        var store = new SqliteSessionStore(
            $"Data Source={path};Pooling=False",
            NullLogger<SqliteSessionStore>.Instance,
            conversations);

        _ = await store.GetAsync(SessionId.From("missing"));

        AssertVersion(path, SqliteSessionStore.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Config_store_records_schema_version_in_both_slots()
    {
        var path = DatabasePath("config.db");
        var store = new SqliteConfigStore($"Data Source={path};Pooling=False");

        _ = await store.ReadEntriesAsync();

        AssertVersion(path, SqliteConfigStore.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Conversation_store_records_schema_version_in_both_slots()
    {
        var path = DatabasePath("conversations.db");
        var store = new SqliteConversationStore(
            $"Data Source={path};Pooling=False",
            NullLogger<SqliteConversationStore>.Instance);

        _ = await store.GetAsync(ConversationId.From("missing"));

        AssertVersion(path, SqliteConversationStore.CurrentSchemaVersion);
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

    private string DatabasePath(string fileName) => Path.Combine(_directory, fileName);

    private static void AssertVersion(string path, int expectedVersion)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using var meta = connection.CreateCommand();
        meta.CommandText = $"SELECT value FROM {SqliteStoreIdentity.TableName} WHERE key = $key;";
        meta.Parameters.AddWithValue("$key", SqliteSchemaVersion.SchemaVersionKey);
        (meta.ExecuteScalar() as string).ShouldBe(
            expectedVersion.ToString(CultureInfo.InvariantCulture));

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(pragma.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(expectedVersion);
    }
}
