using System.Globalization;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Nav;
using BotNexus.Gateway.Tools;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Schema-version contract for independently owned gateway SQLite databases added after #2835.
/// </summary>
public sealed class SqliteOwnedStoreSchemaVersionTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-owned-store-schema-version-").FullName;

    [Fact]
    public async Task Extension_state_store_records_schema_version_in_both_slots()
    {
        var path = DatabasePath("extension-state.db");
        var store = new SqliteExtensionStateStore(path);

        await store.InitializeAsync();

        AssertVersion(path, SqliteExtensionStateStore.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Nav_order_store_records_schema_version_in_both_slots()
    {
        var path = DatabasePath("nav-order.db");
        var store = new SqliteNavOrderStore(path);

        await store.InitializeAsync();

        AssertVersion(path, SqliteNavOrderStore.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Tool_store_records_schema_version_in_both_slots()
    {
        var path = DatabasePath("tools.db");
        var store = new SqliteToolStore(path);

        await store.InitializeAsync();

        AssertVersion(path, SqliteToolStore.CurrentSchemaVersion);
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
