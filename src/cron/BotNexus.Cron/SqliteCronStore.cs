using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Cron;

public sealed class SqliteCronStore(string dbPath, IFileSystem? fileSystem = null) : ICronStore
{
    private readonly string _dbPath = dbPath;
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS cron_jobs (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    schedule TEXT NOT NULL,
                    action_type TEXT NOT NULL,
                    agent_id TEXT NULL,
                    message TEXT NULL,
                    webhook_url TEXT NULL,
                    shell_command TEXT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    time_zone TEXT NULL,
                    created_by TEXT NULL,
                    created_at TEXT NOT NULL,
                    last_run_at TEXT NULL,
                    next_run_at TEXT NULL,
                    last_run_status TEXT NULL,
                    last_run_error TEXT NULL,
                    metadata_json TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_cron_jobs_enabled_next_run_at
                ON cron_jobs(enabled, next_run_at);

                CREATE TABLE IF NOT EXISTS cron_runs (
                    id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    status TEXT NOT NULL,
                    error TEXT NULL,
                    session_id TEXT NULL,
                    FOREIGN KEY(job_id) REFERENCES cron_jobs(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_cron_runs_job_id_started_at
                ON cron_runs(job_id, started_at DESC);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Migrate existing databases: add time_zone column if missing.
            await using var migrate = connection.CreateCommand();
            migrate.CommandText = """
                ALTER TABLE cron_jobs ADD COLUMN time_zone TEXT NULL;
                """;
            try { await migrate.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            catch (SqliteException) { /* column already exists */ }

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var created = job with
            {
                Id = string.IsNullOrWhiteSpace(job.Id) ? Guid.NewGuid().ToString("N") : job.Id,
                CreatedAt = job.CreatedAt == default ? DateTimeOffset.UtcNow : job.CreatedAt
            };

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO cron_jobs (
                    id, name, schedule, action_type, agent_id, message, webhook_url, shell_command,
                    enabled, time_zone, created_by, created_at, last_run_at, next_run_at, last_run_status, last_run_error, metadata_json
                )
                VALUES (
                    $id, $name, $schedule, $actionType, $agentId, $message, $webhookUrl, $shellCommand,
                    $enabled, $timeZone, $createdBy, $createdAt, $lastRunAt, $nextRunAt, $lastRunStatus, $lastRunError, $metadataJson
                )
                """;
            BindJob(command, created);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return created;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CronJob?> GetAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, schedule, action_type, agent_id, message, webhook_url, shell_command,
                   enabled, time_zone, created_by, created_at, last_run_at, next_run_at, last_run_status, last_run_error, metadata_json
            FROM cron_jobs
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", jobId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    public async Task<IReadOnlyList<CronJob>> ListAsync(string? agentId = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, schedule, action_type, agent_id, message, webhook_url, shell_command,
                   enabled, time_zone, created_by, created_at, last_run_at, next_run_at, last_run_status, last_run_error, metadata_json
            FROM cron_jobs
            WHERE $agentId IS NULL OR agent_id = $agentId
            ORDER BY created_at DESC
            """;
        command.Parameters.AddWithValue("$agentId", (object?)agentId ?? DBNull.Value);

        List<CronJob> jobs = [];
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            jobs.Add(ReadJob(reader));

        return jobs;
    }

    public async Task<CronJob> UpdateAsync(CronJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO cron_jobs (
                    id, name, schedule, action_type, agent_id, message, webhook_url, shell_command,
                    enabled, time_zone, created_by, created_at, last_run_at, next_run_at, last_run_status, last_run_error, metadata_json
                )
                VALUES (
                    $id, $name, $schedule, $actionType, $agentId, $message, $webhookUrl, $shellCommand,
                    $enabled, $timeZone, $createdBy, $createdAt, $lastRunAt, $nextRunAt, $lastRunStatus, $lastRunError, $metadataJson
                )
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    schedule = excluded.schedule,
                    action_type = excluded.action_type,
                    agent_id = excluded.agent_id,
                    message = excluded.message,
                    webhook_url = excluded.webhook_url,
                    shell_command = excluded.shell_command,
                    enabled = excluded.enabled,
                    time_zone = excluded.time_zone,
                    created_by = excluded.created_by,
                    created_at = excluded.created_at,
                    last_run_at = excluded.last_run_at,
                    next_run_at = excluded.next_run_at,
                    last_run_status = excluded.last_run_status,
                    last_run_error = excluded.last_run_error,
                    metadata_json = excluded.metadata_json
                """;
            BindJob(command, job);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return job;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var deleteRuns = connection.CreateCommand();
            deleteRuns.CommandText = "DELETE FROM cron_runs WHERE job_id = $jobId";
            deleteRuns.Parameters.AddWithValue("$jobId", jobId);
            await deleteRuns.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var deleteJob = connection.CreateCommand();
            deleteJob.CommandText = "DELETE FROM cron_jobs WHERE id = $jobId";
            deleteJob.Parameters.AddWithValue("$jobId", jobId);
            await deleteJob.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CronRun> RecordRunStartAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var run = new CronRun
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
                StartedAt = now,
                Status = "running"
            };

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var insertRun = connection.CreateCommand();
            insertRun.CommandText = """
                INSERT INTO cron_runs (id, job_id, started_at, completed_at, status, error, session_id)
                VALUES ($id, $jobId, $startedAt, $completedAt, $status, $error, $sessionId)
                """;
            insertRun.Parameters.AddWithValue("$id", run.Id);
            insertRun.Parameters.AddWithValue("$jobId", run.JobId);
            insertRun.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
            insertRun.Parameters.AddWithValue("$completedAt", DBNull.Value);
            insertRun.Parameters.AddWithValue("$status", run.Status);
            insertRun.Parameters.AddWithValue("$error", DBNull.Value);
            insertRun.Parameters.AddWithValue("$sessionId", DBNull.Value);
            await insertRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var updateJob = connection.CreateCommand();
            updateJob.CommandText = """
                UPDATE cron_jobs
                SET last_run_status = 'running',
                    last_run_error = NULL,
                    last_run_at = $lastRunAt
                WHERE id = $jobId
                """;
            updateJob.Parameters.AddWithValue("$lastRunAt", now.ToString("O"));
            updateJob.Parameters.AddWithValue("$jobId", jobId);
            await updateJob.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return run;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordRunCompleteAsync(
        string runId,
        string status,
        string? error = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE cron_runs
                SET completed_at = $completedAt,
                    status = $status,
                    error = $error,
                    session_id = $sessionId
                WHERE id = $runId
                """;
            command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$runId", runId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(string jobId, int limit = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await InitializeAsync(ct).ConfigureAwait(false);

        var cappedLimit = Math.Clamp(limit, 1, int.MaxValue);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, job_id, started_at, completed_at, status, error, session_id
            FROM cron_runs
            WHERE job_id = $jobId
            ORDER BY started_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$limit", cappedLimit);

        List<CronRun> runs = [];
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            runs.Add(ReadRun(reader));

        return runs;
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static void BindJob(SqliteCommand command, CronJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$schedule", job.Schedule);
        command.Parameters.AddWithValue("$actionType", job.ActionType);
        command.Parameters.AddWithValue("$agentId", (object?)job.AgentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)job.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$webhookUrl", (object?)job.WebhookUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$shellCommand", (object?)job.ShellCommand ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", job.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$timeZone", (object?)job.TimeZone ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdBy", (object?)job.CreatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastRunAt", ToNullableString(job.LastRunAt));
        command.Parameters.AddWithValue("$nextRunAt", ToNullableString(job.NextRunAt));
        command.Parameters.AddWithValue("$lastRunStatus", (object?)job.LastRunStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastRunError", (object?)job.LastRunError ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", SerializeMetadata(job.Metadata));
    }

    private static CronJob ReadJob(SqliteDataReader reader)
    {
        var metadataJson = reader.IsDBNull(16) ? null : reader.GetString(16);
        return new CronJob
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Schedule = reader.GetString(2),
            ActionType = reader.GetString(3),
            AgentId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Message = reader.IsDBNull(5) ? null : reader.GetString(5),
            WebhookUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
            ShellCommand = reader.IsDBNull(7) ? null : reader.GetString(7),
            Enabled = !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
            TimeZone = reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedBy = reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAt = ParseDate(reader.GetString(11)),
            LastRunAt = reader.IsDBNull(12) ? null : ParseDate(reader.GetString(12)),
            NextRunAt = reader.IsDBNull(13) ? null : ParseDate(reader.GetString(13)),
            LastRunStatus = reader.IsDBNull(14) ? null : reader.GetString(14),
            LastRunError = reader.IsDBNull(15) ? null : reader.GetString(15),
            Metadata = DeserializeMetadata(metadataJson)
        };
    }

    private static CronRun ReadRun(SqliteDataReader reader)
    {
        return new CronRun
        {
            Id = reader.GetString(0),
            JobId = reader.GetString(1),
            StartedAt = ParseDate(reader.GetString(2)),
            CompletedAt = reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
            Status = reader.GetString(4),
            Error = reader.IsDBNull(5) ? null : reader.GetString(5),
            SessionId = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }

    private static object ToNullableString(DateTimeOffset? value)
        => value is null ? DBNull.Value : value.Value.ToString("O");

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;

    private static object SerializeMetadata(IReadOnlyDictionary<string, object?>? metadata)
        => metadata is null ? DBNull.Value : JsonSerializer.Serialize(metadata, JsonOptions);

    private static IReadOnlyDictionary<string, object?>? DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, JsonOptions);
    }
}
