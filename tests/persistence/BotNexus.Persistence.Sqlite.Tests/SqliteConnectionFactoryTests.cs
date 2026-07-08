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
        SqliteConnection.ClearAllPools();
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

    private static async Task<long> ReadBusyTimeoutAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(value);
    }

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
