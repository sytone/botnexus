using BotNexus.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests;

public sealed class DebugDbCommandTests : IDisposable
{
    private readonly string _tempDir;

    public DebugDbCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        CreateTestDatabases();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void CreateTestDatabases()
    {
        // sessions.db
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "sessions.db")}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sessions (id TEXT PRIMARY KEY, status TEXT);
                INSERT INTO sessions VALUES ('s1', 'active');
                INSERT INTO sessions VALUES ('s2', 'sealed');
                CREATE TABLE session_history (id INTEGER PRIMARY KEY, session_id TEXT, content TEXT);
                INSERT INTO session_history VALUES (1, 's1', 'hello');
                INSERT INTO session_history VALUES (2, 's1', 'world');
                INSERT INTO session_history VALUES (3, 's2', 'done');
                """;
            cmd.ExecuteNonQuery();
        }

        // cron.db
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "cron.db")}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE jobs (id TEXT PRIMARY KEY, name TEXT, schedule TEXT);
                INSERT INTO jobs VALUES ('j1', 'maintenance', '0 * * * *');
                CREATE TABLE run_history (id INTEGER PRIMARY KEY, job_id TEXT, status TEXT);
                INSERT INTO run_history VALUES (1, 'j1', 'success');
                """;
            cmd.ExecuteNonQuery();
        }
    }

    // ─── tables ────────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteTables_returns_tables_for_all_databases()
    {
        var result = DebugDbCommand.ExecuteTables(_tempDir, null, "json");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExecuteTables_filters_by_db_name()
    {
        var result = DebugDbCommand.ExecuteTables(_tempDir, "sessions", "json");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExecuteTables_returns_error_for_missing_directory()
    {
        var result = DebugDbCommand.ExecuteTables(Path.Combine(_tempDir, "nonexistent"), null, "table");
        Assert.Equal(1, result);
    }

    [Fact]
    public void ExecuteTables_returns_error_when_specific_db_missing()
    {
        var result = DebugDbCommand.ExecuteTables(_tempDir, "memory", "table");
        // memory.db doesn't exist, but we should still get table format (skipped warning)
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExecuteTables_table_format_succeeds()
    {
        var result = DebugDbCommand.ExecuteTables(_tempDir, "cron", "table");
        Assert.Equal(0, result);
    }

    // ─── schema ────────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteSchema_returns_ddl_for_all_databases()
    {
        var result = DebugDbCommand.ExecuteSchema(_tempDir, null, "json");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExecuteSchema_filters_by_db_name()
    {
        var result = DebugDbCommand.ExecuteSchema(_tempDir, "cron", "json");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExecuteSchema_returns_error_for_missing_directory()
    {
        var result = DebugDbCommand.ExecuteSchema(Path.Combine(_tempDir, "nonexistent"), null, "table");
        Assert.Equal(1, result);
    }

    [Fact]
    public void ExecuteSchema_table_format_succeeds()
    {
        var result = DebugDbCommand.ExecuteSchema(_tempDir, "sessions", "table");
        Assert.Equal(0, result);
    }

    // ─── size ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteSize_returns_sizes_for_all_db_files()
    {
        var result = DebugDbCommand.ExecuteSize(_tempDir, "json");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExecuteSize_table_format_succeeds()
    {
        var result = DebugDbCommand.ExecuteSize(_tempDir, "table");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExecuteSize_returns_error_for_missing_directory()
    {
        var result = DebugDbCommand.ExecuteSize(Path.Combine(_tempDir, "nonexistent"), "table");
        Assert.Equal(1, result);
    }

    // ─── utility methods ───────────────────────────────────────────────────

    [Fact]
    public void ResolveDbFiles_returns_all_dbs_when_no_filter()
    {
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, null);
        Assert.Equal(2, files.Length); // sessions.db + cron.db
    }

    [Fact]
    public void ResolveDbFiles_filters_to_single_db()
    {
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, "sessions");
        Assert.Single(files);
        Assert.Equal("sessions", files[0].Name);
    }

    [Fact]
    public void ResolveDbFiles_handles_db_extension_in_name()
    {
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, "cron.db");
        Assert.Single(files);
        Assert.Equal("cron.db", files[0].Name);
    }

    [Fact]
    public void ResolveDbFiles_returns_empty_for_nonexistent_home()
    {
        var files = DebugDbCommand.ResolveDbFiles(Path.Combine(_tempDir, "nonexistent"), null);
        Assert.Empty(files);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741824, "1.00 GB")]
    public void FormatSize_returns_expected_string(long bytes, string expected)
    {
        Assert.Equal(expected, DebugDbCommand.FormatSize(bytes));
    }
}
