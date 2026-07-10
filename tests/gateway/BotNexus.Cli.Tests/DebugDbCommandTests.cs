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

    private static void CreateDb(string path, string ddl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
    }

    private void CreateTestDatabases()
    {
        // sessions.db (home root, .db extension)
        CreateDb(Path.Combine(_tempDir, "sessions.db"), """
            CREATE TABLE sessions (id TEXT PRIMARY KEY, status TEXT);
            INSERT INTO sessions VALUES ('s1', 'active');
            INSERT INTO sessions VALUES ('s2', 'sealed');
            CREATE TABLE session_history (id INTEGER PRIMARY KEY, session_id TEXT, content TEXT);
            INSERT INTO session_history VALUES (1, 's1', 'hello');
            INSERT INTO session_history VALUES (2, 's1', 'world');
            INSERT INTO session_history VALUES (3, 's2', 'done');
            """);

        // cron.sqlite (home root, .sqlite extension — previously invisible)
        CreateDb(Path.Combine(_tempDir, "cron.sqlite"), """
            CREATE TABLE jobs (id TEXT PRIMARY KEY, name TEXT, schedule TEXT);
            INSERT INTO jobs VALUES ('j1', 'maintenance', '0 * * * *');
            CREATE TABLE run_history (id INTEGER PRIMARY KEY, job_id TEXT, status TEXT);
            INSERT INTO run_history VALUES (1, 'j1', 'success');
            """);

        // webhooks.sqlite (home root, .sqlite extension — previously invisible)
        CreateDb(Path.Combine(_tempDir, "webhooks.sqlite"), """
            CREATE TABLE webhook_registrations (id TEXT PRIMARY KEY, agent_id TEXT);
            INSERT INTO webhook_registrations VALUES ('w1', 'farnsworth');
            """);

        // data/skill-usage.db (data subfolder — previously invisible)
        CreateDb(Path.Combine(_tempDir, "data", "skill-usage.db"), """
            CREATE TABLE usage_entity (namespace TEXT, key TEXT);
            INSERT INTO usage_entity VALUES ('skills', 'github');
            """);

        // agents/<id>/data/memory.sqlite (per-agent — opt-in via --include-agents)
        CreateDb(Path.Combine(_tempDir, "agents", "farnsworth", "data", "memory.sqlite"), """
            CREATE TABLE memory_entries (id TEXT PRIMARY KEY, content TEXT);
            INSERT INTO memory_entries VALUES ('m1', 'note');
            """);
    }

    // ─── discovery ─────────────────────────────────────────────────────────

    [Fact]
    public void ResolveDbFiles_discovers_both_extensions_and_data_subfolder()
    {
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, null);
        var names = files.Select(f => f.Name).ToArray();

        // Home root .db + .sqlite + data/ subfolder — but NOT per-agent (opt-in).
        Assert.Contains("sessions", names);
        Assert.Contains("cron", names);
        Assert.Contains("webhooks", names);
        Assert.Contains("data/skill-usage", names);
        Assert.DoesNotContain(names, n => n.Contains("memory"));
        Assert.Equal(4, files.Length);
    }

    [Fact]
    public void ResolveDbFiles_includes_agent_memory_when_opted_in()
    {
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, null, includeAgents: true);
        var names = files.Select(f => f.Name).ToArray();

        Assert.Contains("farnsworth/memory", names);
        Assert.Equal(5, files.Length);
    }

    [Fact]
    public void ResolveDbFiles_filters_to_single_db_by_bare_name()
    {
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, "sessions");
        Assert.Single(files);
        Assert.Equal("sessions", files[0].Name);
    }

    [Fact]
    public void ResolveDbFiles_filters_sqlite_extension_db_by_bare_name()
    {
        // cron is a .sqlite file — must resolve from the bare "cron" name.
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, "cron");
        Assert.Single(files);
        Assert.Equal("cron", files[0].Name);
    }

    [Fact]
    public void ResolveDbFiles_filters_with_explicit_sqlite_extension()
    {
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, "webhooks.sqlite");
        Assert.Single(files);
        Assert.Equal("webhooks", files[0].Name);
    }

    [Fact]
    public void ResolveDbFiles_returns_probe_for_unknown_db()
    {
        // memory not present at root — still returns a single probe entry so the
        // caller emits a "skipped: file not found" warning rather than "no dbs".
        var files = DebugDbCommand.ResolveDbFiles(_tempDir, "nonexistent");
        Assert.Single(files);
    }

    [Fact]
    public void ResolveDbFiles_returns_empty_for_nonexistent_home()
    {
        var files = DebugDbCommand.ResolveDbFiles(Path.Combine(_tempDir, "nonexistent"), null);
        Assert.Empty(files);
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
    public void ExecuteTables_includes_agents_when_opted_in()
    {
        var result = DebugDbCommand.ExecuteTables(_tempDir, null, "json", includeAgents: true);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExecuteTables_returns_error_for_missing_directory()
    {
        var result = DebugDbCommand.ExecuteTables(Path.Combine(_tempDir, "nonexistent"), null, "table");
        Assert.Equal(1, result);
    }

    [Fact]
    public void ExecuteTables_returns_zero_when_specific_db_missing()
    {
        var result = DebugDbCommand.ExecuteTables(_tempDir, "memory", "table");
        // memory not at root; probe entry skipped with warning, still exit 0.
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
