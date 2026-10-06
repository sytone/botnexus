using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite.Tests;

public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dir;

    public SqliteConnectionFactoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-connfactory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_dir);
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a lingering handle on Windows must not fail the test.
        }
    }

    private string DbPath => Path.Combine(_dir, "factory.db");

    private static async Task<long> ReadPragmaAsync(SqliteConnection connection, string pragma)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma};";
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(value);
    }

    private static Task<long> ReadBusyTimeoutAsync(SqliteConnection connection)
        => ReadPragmaAsync(connection, "busy_timeout");

    private static Task<long> ReadForeignKeysAsync(SqliteConnection connection)
        => ReadPragmaAsync(connection, "foreign_keys");

    [Fact]
    public void DefaultBusyTimeoutMs_is_5000()
    {
        SqliteConnectionFactory.DefaultBusyTimeoutMs.ShouldBe(5000);
    }

    [Fact]
    public async Task Create_applies_default_busy_timeout_on_open()
    {
        await using var connection = SqliteConnectionFactory.Create($"Data Source={DbPath}");
        await connection.OpenAsync();

        var timeout = await ReadBusyTimeoutAsync(connection);

        timeout.ShouldBe(SqliteConnectionFactory.DefaultBusyTimeoutMs);
    }

    [Fact]
    public async Task Create_reapplies_busy_timeout_after_reopen()
    {
        await using var connection = SqliteConnectionFactory.Create($"Data Source={DbPath}");
        await connection.OpenAsync();
        await connection.CloseAsync();
        await connection.OpenAsync();

        var timeout = await ReadBusyTimeoutAsync(connection);

        timeout.ShouldBe(SqliteConnectionFactory.DefaultBusyTimeoutMs);
    }

    [Fact]
    public async Task Create_enables_foreign_keys_on_open_and_reopen()
    {
        await using var connection = SqliteConnectionFactory.Create($"Data Source={DbPath}");
        await connection.OpenAsync();
        (await ReadForeignKeysAsync(connection)).ShouldBe(1);

        await connection.CloseAsync();
        await connection.OpenAsync();

        (await ReadForeignKeysAsync(connection)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_validates_foreign_keys_once_per_schema_generation()
    {
        var validationCount = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BotNexus.Persistence.Sqlite",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "sqlite.foreign_key_validation")
                {
                    Interlocked.Increment(ref validationCount);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using (var first = SqliteConnectionFactory.Create($"Data Source={DbPath}"))
        {
            await first.OpenAsync();
            await first.CloseAsync();
            await first.OpenAsync();
        }

        await using (var second = SqliteConnectionFactory.Create($"Data Source={DbPath}"))
        {
            await second.OpenAsync();
            await using var schemaChange = second.CreateCommand();
            schemaChange.CommandText = "CREATE TABLE schema_generation_two (id INTEGER PRIMARY KEY);";
            await schemaChange.ExecuteNonQueryAsync();
        }

        await using (var third = SqliteConnectionFactory.Create($"Data Source={DbPath}"))
        {
            await third.OpenAsync();
            await third.CloseAsync();
            await third.OpenAsync();
        }

        validationCount.ShouldBe(2);
        File.ReadAllText(DbPath + ".botnexus-fk-validation").ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Create_does_not_mutate_a_read_only_store_after_validation()
    {
        await using (var seed = SqliteConnectionFactory.Create($"Data Source={DbPath}"))
        {
            await seed.OpenAsync();
            await using var schema = seed.CreateCommand();
            schema.CommandText = "CREATE TABLE item (id INTEGER PRIMARY KEY);";
            await schema.ExecuteNonQueryAsync();
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(DbPath);
        await using var readOnly = SqliteConnectionFactory.Create($"Data Source={DbPath};Mode=ReadOnly");
        await readOnly.OpenAsync();

        File.GetLastWriteTimeUtc(DbPath).ShouldBe(lastWriteUtc);
    }

    [Fact]
    public async Task Create_rejects_a_store_with_existing_foreign_key_violations()
    {
        await using (var seed = new SqliteConnection($"Data Source={DbPath};Foreign Keys=False"))
        {
            await seed.OpenAsync();
            await using var schema = seed.CreateCommand();
            schema.CommandText = """
                CREATE TABLE parent (id INTEGER PRIMARY KEY);
                CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
                INSERT INTO child (id, parent_id) VALUES (1, 404);
                """;
            await schema.ExecuteNonQueryAsync();
        }

        await using var connection = SqliteConnectionFactory.Create($"Data Source={DbPath};Foreign Keys=False");
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await connection.OpenAsync());

        exception.Message.ShouldContain("child");
        exception.Message.ShouldContain("foreign key");
        exception.Message.ShouldContain("remediate", Case.Insensitive);
    }

    [Fact]
    public async Task Create_enforces_foreign_keys_for_cross_process_style_writers()
    {
        await using var owner = SqliteConnectionFactory.Create($"Data Source={DbPath};Foreign Keys=False");
        await owner.OpenAsync();
        await using (var schema = owner.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE parent (id INTEGER PRIMARY KEY);
                CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
                """;
            await schema.ExecuteNonQueryAsync();
        }

        await using var writer = SqliteConnectionFactory.Create($"Data Source={DbPath};Foreign Keys=False");
        await writer.OpenAsync();
        await using var invalidInsert = writer.CreateCommand();
        invalidInsert.CommandText = "INSERT INTO child (id, parent_id) VALUES (1, 404);";

        var exception = await Should.ThrowAsync<SqliteException>(
            async () => await invalidInsert.ExecuteNonQueryAsync());
        exception.SqliteErrorCode.ShouldBe(19);
    }

    [Fact]
    public async Task Create_honours_custom_busy_timeout()
    {
        await using var connection = SqliteConnectionFactory.Create($"Data Source={DbPath}", busyTimeoutMs: 1234);
        await connection.OpenAsync();

        var timeout = await ReadBusyTimeoutAsync(connection);

        timeout.ShouldBe(1234);
    }

    [Fact]
    public async Task AttachBusyTimeout_applies_pragma_on_open_for_existing_connection()
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        SqliteConnectionFactory.AttachBusyTimeout(connection);
        await connection.OpenAsync();

        var timeout = await ReadBusyTimeoutAsync(connection);

        timeout.ShouldBe(SqliteConnectionFactory.DefaultBusyTimeoutMs);
    }

    [Fact]
    public void Create_rejects_negative_timeout()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            SqliteConnectionFactory.Create("Data Source=:memory:", busyTimeoutMs: -1));
    }
}
