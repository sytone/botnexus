using Microsoft.Data.Sqlite;
using BotNexus.Persistence.Sqlite.Telemetry;

namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Clause 1 of #2835 at a REAL store rather than at the runner: initialising
/// <see cref="SqliteUsageTelemetryStore"/> must leave a schema version on disk in both slots.
/// </summary>
/// <remarks>
/// The runner's own tests prove the mechanism; this proves a store actually calls it. Those are
/// different claims, and only the second one fails if a store adopts the constant but forgets the
/// call - the exact omission this feature exists to make impossible.
/// </remarks>
public sealed class SqliteUsageTelemetryStoreSchemaVersionTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("botnexus-usage-schema-version-").FullName;

    private string DbPath => Path.Combine(_dir, "usage.db");

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_dir);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked WAL file on Windows must not fail the test run.
        }
    }

    [Fact]
    public async Task Initialised_store_records_its_schema_version_in_both_slots()
    {
        await using (var store = new SqliteUsageTelemetryStore(DbPath))
        {
            await store.IncrementAsync("skills", "some-skill", "invocations");
        }

        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        using var meta = connection.CreateCommand();
        meta.CommandText = $"SELECT value FROM {SqliteStoreIdentity.TableName} WHERE key = $k;";
        meta.Parameters.AddWithValue("$k", SqliteSchemaVersion.SchemaVersionKey);
        Assert.Equal(
            SqliteUsageTelemetryStore.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            meta.ExecuteScalar() as string);

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA user_version;";
        Assert.Equal(
            SqliteUsageTelemetryStore.CurrentSchemaVersion,
            Convert.ToInt32(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A store on disk written by a FUTURE build is refused, at the real store's initialisation path,
    /// with the store path in the message.
    /// </summary>
    [Fact]
    public async Task Store_written_by_a_newer_schema_is_refused_on_open()
    {
        await using (var store = new SqliteUsageTelemetryStore(DbPath))
        {
            await store.IncrementAsync("skills", "some-skill", "invocations");
        }

        var future = SqliteUsageTelemetryStore.CurrentSchemaVersion + 5;
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText =
                $"UPDATE {SqliteStoreIdentity.TableName} SET value = $v WHERE key = $k;";
            update.Parameters.AddWithValue("$v", future.ToString(System.Globalization.CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$k", SqliteSchemaVersion.SchemaVersionKey);
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        SqlitePoolCleanup.ClearPoolFor(DbPath);

        await using var reopened = new SqliteUsageTelemetryStore(DbPath);
        var error = await Assert.ThrowsAsync<SqliteSchemaVersionMismatchException>(
            () => reopened.IncrementAsync("skills", "some-skill", "invocations"));

        Assert.Equal(future, error.StoreVersion);
        Assert.Equal(SqliteUsageTelemetryStore.CurrentSchemaVersion, error.CodeVersion);
        Assert.Contains(DbPath, error.Message, StringComparison.Ordinal);
    }
}
