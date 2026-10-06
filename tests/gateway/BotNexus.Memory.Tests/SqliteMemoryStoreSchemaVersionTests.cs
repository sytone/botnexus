using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using BotNexus.Persistence.Sqlite;

namespace BotNexus.Memory.Tests;

public sealed class SqliteMemoryStoreSchemaVersionTests
{
    [Fact]
    public async Task InitializeAsync_ConvergesLegacyVersionTwoMetadataAndPragma()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        var (legacyVersion, metadataVersion, pragmaVersion) = await ReadVersionsAsync(context.DbPath);

        legacyVersion.ShouldBe(2);
        metadataVersion.ShouldBe(2);
        pragmaVersion.ShouldBe(2);
    }

    [Fact]
    public async Task InitializeAsync_AdoptsUnversionedLegacyStore_WithoutChangingLegacyVersionOrFts()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "legacy-memory", "agent", "adoptionpreservessearchablememory"));
        await SetSharedVersionAsync(context.DbPath, null, 0);

        await new SqliteMemoryStore(context.DbPath).InitializeAsync();

        var (legacyVersion, metadataVersion, pragmaVersion) = await ReadVersionsAsync(context.DbPath);
        legacyVersion.ShouldBe(2);
        metadataVersion.ShouldBe(2);
        pragmaVersion.ShouldBe(2);

        await using var reopened = new SqliteMemoryStore(context.DbPath);
        var results = await reopened.SearchAsync("adoptionpreservessearchablememory");
        results.ShouldHaveSingleItem().Id.ShouldBe("legacy-memory");
    }

    [Fact]
    public async Task InitializeAsync_RefusesNewerSchema_WithoutChangingAnyVersionStamp()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await SetSharedVersionAsync(context.DbPath, 3, 3);
        var store = new SqliteMemoryStore(context.DbPath);

        Func<Task> act = () => store.InitializeAsync();
        var exception = await Should.ThrowAsync<SqliteSchemaVersionMismatchException>(act);

        exception.StoreVersion.ShouldBe(3);
        exception.CodeVersion.ShouldBe(2);
        var (legacyVersion, metadataVersion, pragmaVersion) = await ReadVersionsAsync(context.DbPath);
        legacyVersion.ShouldBe(2);
        metadataVersion.ShouldBe(3);
        pragmaVersion.ShouldBe(3);
    }

    [Fact]
    public async Task InitializeAsync_ForwardMigration_ConvergesBothSharedStampsTransactionally()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await SetSharedVersionAsync(context.DbPath, 1, 1);
        var store = new SqliteMemoryStore(context.DbPath);

        await store.InitializeAsync();

        var (legacyVersion, metadataVersion, pragmaVersion) = await ReadVersionsAsync(context.DbPath);
        legacyVersion.ShouldBe(2);
        metadataVersion.ShouldBe(2);
        pragmaVersion.ShouldBe(2);
    }

    private static async Task<(int Legacy, int Metadata, int Pragma)> ReadVersionsAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version LIMIT 1";
        var legacyVersion = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "SELECT value FROM store_meta WHERE key = 'schema_version'";
        var metadataVersion = int.Parse(
            (string)(await command.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA user_version";
        var pragmaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        return (legacyVersion, metadataVersion, pragmaVersion);
    }

    private static async Task SetSharedVersionAsync(string dbPath, int? metadataVersion, int pragmaVersion)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        if (metadataVersion is { } version)
        {
            command.CommandText = """
                INSERT INTO store_meta (key, value) VALUES ('schema_version', $version)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            command.Parameters.AddWithValue(
                "$version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
            command.Parameters.Clear();
        }
        else
        {
            command.CommandText = "DELETE FROM store_meta WHERE key = 'schema_version'";
            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = $"PRAGMA user_version = {pragmaVersion}";
        await command.ExecuteNonQueryAsync();
    }
}
