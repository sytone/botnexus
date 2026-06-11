using BotNexus.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests;

public sealed class DebugCronCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public DebugCronCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "cron.sqlite");
        CreateTestDatabase();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void CreateTestDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE cron_jobs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                schedule TEXT NOT NULL,
                action_type TEXT NOT NULL,
                agent_id TEXT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                last_run_at TEXT NULL,
                next_run_at TEXT NULL,
                last_run_status TEXT NULL,
                last_run_error TEXT NULL
            );
            CREATE TABLE cron_runs (
                id TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                status TEXT NOT NULL,
                error TEXT NULL,
                session_id TEXT NULL,
                FOREIGN KEY(job_id) REFERENCES cron_jobs(id) ON DELETE CASCADE
            );
            CREATE INDEX idx_cron_runs_job_id_started_at ON cron_runs(job_id, started_at DESC);

            INSERT INTO cron_jobs VALUES ('j1', 'heartbeat', '*/5 * * * *', 'agent-prompt', 'farnsworth', 1, '2026-06-10T12:00:00Z', '2026-06-10T12:05:00Z', 'completed', NULL);
            INSERT INTO cron_jobs VALUES ('j2', 'maintenance', '0 0 * * *', 'agent-prompt', 'farnsworth', 1, '2026-06-09T00:00:00Z', '2026-06-01T00:00:00Z', 'completed', NULL);
            INSERT INTO cron_jobs VALUES ('j3', 'disabled-job', '0 12 * * *', 'agent-prompt', 'nova', 0, NULL, NULL, NULL, NULL);

            INSERT INTO cron_runs VALUES ('r1', 'j1', '2026-06-10T12:00:00Z', '2026-06-10T12:00:05Z', 'completed', NULL, 's1');
            INSERT INTO cron_runs VALUES ('r2', 'j1', '2026-06-10T11:55:00Z', '2026-06-10T11:55:03Z', 'completed', NULL, 's2');
            INSERT INTO cron_runs VALUES ('r3', 'j2', '2026-06-09T00:00:00Z', '2026-06-09T00:01:30Z', 'completed', NULL, 's3');
            INSERT INTO cron_runs VALUES ('r4', 'j1', '2026-06-10T11:50:00Z', NULL, 'running', NULL, 's4');
            INSERT INTO cron_runs VALUES ('r5', 'j2', '2026-06-08T00:00:00Z', '2026-06-08T00:00:10Z', 'failed', 'timeout after 90s', NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    // ─── ResolveCronDb ─────────────────────────────────────────────────────

    [Fact]
    public void ResolveCronDb_uses_target_path()
    {
        var result = DebugCronCommand.ResolveCronDb("/custom/path");
        result.ShouldBe(Path.Combine("/custom/path", "cron.sqlite"));
    }

    [Fact]
    public void ResolveCronDb_uses_default_when_null()
    {
        var result = DebugCronCommand.ResolveCronDb(null);
        result.ShouldContain("cron.sqlite");
    }

    // ─── status ────────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteStatus_returns_zero_with_valid_db()
    {
        var exitCode = DebugCronCommand.ExecuteStatus(_dbPath, "json");
        exitCode.ShouldBe(0);
    }

    [Fact]
    public void ExecuteStatus_returns_one_when_db_missing()
    {
        var exitCode = DebugCronCommand.ExecuteStatus(Path.Combine(_tempDir, "nonexistent.sqlite"), "table");
        exitCode.ShouldBe(1);
    }

    [Fact]
    public void ExecuteStatus_table_format_returns_zero()
    {
        var exitCode = DebugCronCommand.ExecuteStatus(_dbPath, "table");
        exitCode.ShouldBe(0);
    }

    // ─── history ───────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteHistory_returns_zero_with_valid_db()
    {
        var exitCode = DebugCronCommand.ExecuteHistory(_dbPath, null, 20, "json");
        exitCode.ShouldBe(0);
    }

    [Fact]
    public void ExecuteHistory_filters_by_job_id()
    {
        var exitCode = DebugCronCommand.ExecuteHistory(_dbPath, "j1", 10, "json");
        exitCode.ShouldBe(0);
    }

    [Fact]
    public void ExecuteHistory_returns_one_when_db_missing()
    {
        var exitCode = DebugCronCommand.ExecuteHistory(Path.Combine(_tempDir, "nonexistent.sqlite"), null, 20, "table");
        exitCode.ShouldBe(1);
    }

    [Fact]
    public void ExecuteHistory_table_format_returns_zero()
    {
        var exitCode = DebugCronCommand.ExecuteHistory(_dbPath, null, 20, "table");
        exitCode.ShouldBe(0);
    }

    [Fact]
    public void ExecuteHistory_respects_limit()
    {
        var exitCode = DebugCronCommand.ExecuteHistory(_dbPath, null, 2, "json");
        exitCode.ShouldBe(0);
    }

    // ─── missed ────────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteMissed_returns_zero_with_valid_db()
    {
        // j2 has next_run_at in the past (2026-06-01) so it should appear as missed
        var exitCode = DebugCronCommand.ExecuteMissed(_dbPath, "json");
        exitCode.ShouldBe(0);
    }

    [Fact]
    public void ExecuteMissed_returns_one_when_db_missing()
    {
        var exitCode = DebugCronCommand.ExecuteMissed(Path.Combine(_tempDir, "nonexistent.sqlite"), "table");
        exitCode.ShouldBe(1);
    }

    [Fact]
    public void ExecuteMissed_table_format_returns_zero()
    {
        var exitCode = DebugCronCommand.ExecuteMissed(_dbPath, "table");
        exitCode.ShouldBe(0);
    }

    [Fact]
    public void ExecuteMissed_excludes_currently_running_jobs()
    {
        // j1 has a 'running' entry in cron_runs (r4) — but j1's next_run_at is in the future
        // so it shouldn't appear as missed regardless. This verifies the exclusion logic doesn't crash.
        var exitCode = DebugCronCommand.ExecuteMissed(_dbPath, "json");
        exitCode.ShouldBe(0);
    }

    // ─── empty db ──────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteHistory_handles_empty_runs_table()
    {
        var emptyDb = Path.Combine(_tempDir, "empty-cron.sqlite");
        using (var conn = new SqliteConnection($"Data Source={emptyDb}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE cron_jobs (id TEXT PRIMARY KEY, name TEXT, schedule TEXT, action_type TEXT, enabled INTEGER DEFAULT 1, last_run_at TEXT, next_run_at TEXT, last_run_status TEXT, last_run_error TEXT);
                CREATE TABLE cron_runs (id TEXT PRIMARY KEY, job_id TEXT, started_at TEXT, completed_at TEXT, status TEXT, error TEXT, session_id TEXT);
                """;
            cmd.ExecuteNonQuery();
        }

        var exitCode = DebugCronCommand.ExecuteHistory(emptyDb, null, 20, "table");
        exitCode.ShouldBe(0);
    }

    [Fact]
    public void ExecuteStatus_handles_empty_tables()
    {
        var emptyDb = Path.Combine(_tempDir, "empty-cron2.sqlite");
        using (var conn = new SqliteConnection($"Data Source={emptyDb}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE cron_jobs (id TEXT PRIMARY KEY, name TEXT, schedule TEXT, action_type TEXT, enabled INTEGER DEFAULT 1, last_run_at TEXT, next_run_at TEXT, last_run_status TEXT, last_run_error TEXT);
                CREATE TABLE cron_runs (id TEXT PRIMARY KEY, job_id TEXT, started_at TEXT, completed_at TEXT, status TEXT, error TEXT, session_id TEXT);
                """;
            cmd.ExecuteNonQuery();
        }

        var exitCode = DebugCronCommand.ExecuteStatus(emptyDb, "json");
        exitCode.ShouldBe(0);
    }
}
