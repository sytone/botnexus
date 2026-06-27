using System.Text.Json;
using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Cron;

public sealed class SqliteCronStore(string dbPath, IFileSystem? fileSystem = null, ILogger<SqliteCronStore>? logger = null) : ICronStore
{
    private readonly string _dbPath = dbPath;
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly ILogger<SqliteCronStore> _logger = logger ?? NullLogger<SqliteCronStore>.Instance;
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
                    template_name TEXT NULL,
                    template_parameters_json TEXT NULL,
                    model TEXT NULL,
                    webhook_url TEXT NULL,
                    shell_command TEXT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    system INTEGER NOT NULL DEFAULT 0,
                    time_zone TEXT NULL,
                    created_by TEXT NULL,
                    created_at TEXT NOT NULL,
                    last_run_at TEXT NULL,
                    next_run_at TEXT NULL,
                    last_run_status TEXT NULL,
                    last_run_error TEXT NULL,
                    metadata_json TEXT NULL,
                    conversation_id TEXT NULL,
                    delete_after_run INTEGER NOT NULL DEFAULT 0
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

            // Migrate existing databases: add system column if missing.
            await using var migrateSystem = connection.CreateCommand();
            migrateSystem.CommandText = """
                ALTER TABLE cron_jobs ADD COLUMN system INTEGER NOT NULL DEFAULT 0;
                """;
            try { await migrateSystem.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            catch (SqliteException) { /* column already exists */ }

            // Migrate existing databases: add model column if missing.
            await using var migrateModel = connection.CreateCommand();
            migrateModel.CommandText = """
                ALTER TABLE cron_jobs ADD COLUMN model TEXT NULL;
                """;
            try { await migrateModel.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            catch (SqliteException) { /* column already exists */ }

            // Migrate existing databases: add template_name column if missing.
            await using var migrateTemplateName = connection.CreateCommand();
            migrateTemplateName.CommandText = """
                ALTER TABLE cron_jobs ADD COLUMN template_name TEXT NULL;
                """;
            try { await migrateTemplateName.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            catch (SqliteException) { /* column already exists */ }

            // Migrate existing databases: add template_parameters_json column if missing.
            await using var migrateTemplateParameters = connection.CreateCommand();
            migrateTemplateParameters.CommandText = """
                ALTER TABLE cron_jobs ADD COLUMN template_parameters_json TEXT NULL;
                """;
            try { await migrateTemplateParameters.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            catch (SqliteException) { /* column already exists */ }

            // Migrate existing databases: add conversation_id column if missing (P9-D).
            // CronJob.ConversationId is the canonical link from a cron job to its conversation;
            // pre-P9-D rows stored the link only on the in-memory record and lost it on restart.
            await using var migrateConversationId = connection.CreateCommand();
            migrateConversationId.CommandText = """
                ALTER TABLE cron_jobs ADD COLUMN conversation_id TEXT NULL;
                """;
            try { await migrateConversationId.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            catch (SqliteException) { /* column already exists */ }

            // Migrate existing databases: add delete_after_run column if missing (#1561).
            // Opt-in ephemeral-run cleanup flag; pre-existing rows default to 0 (no auto-delete),
            // preserving the long-lived-session behaviour every current job relies on.
            await using var migrateDeleteAfterRun = connection.CreateCommand();
            migrateDeleteAfterRun.CommandText = """
                ALTER TABLE cron_jobs ADD COLUMN delete_after_run INTEGER NOT NULL DEFAULT 0;
                """;
            try { await migrateDeleteAfterRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
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
                CreatedAt = job.CreatedAt == default ? DateTimeOffset.UtcNow : job.CreatedAt
            };

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO cron_jobs (
                    id, name, schedule, action_type, agent_id, message, template_name, template_parameters_json, model, webhook_url, shell_command,
                    enabled, system, time_zone, created_by, created_at, last_run_at, next_run_at, last_run_status, last_run_error, metadata_json, conversation_id, delete_after_run
                )
                VALUES (
                    $id, $name, $schedule, $actionType, $agentId, $message, @templateName, @templateParametersJson, $model, $webhookUrl, $shellCommand,
                    $enabled, $system, $timeZone, $createdBy, $createdAt, $lastRunAt, $nextRunAt, $lastRunStatus, $lastRunError, $metadataJson, $conversationId, $deleteAfterRun
                )
                """;
            BindJob(command, created);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Created cron job '{JobId}' (action={ActionType}, enabled={Enabled}, createdBy={CreatedBy}).",
                created.Id,
                created.ActionType,
                created.Enabled,
                created.CreatedBy);
            return created;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, schedule, action_type, agent_id, message, template_name, template_parameters_json, model, webhook_url, shell_command,
                   enabled, system, time_zone, created_by, created_at, last_run_at, next_run_at, last_run_status, last_run_error, metadata_json, conversation_id, delete_after_run
            FROM cron_jobs
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", jobId.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    public async Task<IReadOnlyList<CronJob>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, schedule, action_type, agent_id, message, template_name, template_parameters_json, model, webhook_url, shell_command,
                   enabled, system, time_zone, created_by, created_at, last_run_at, next_run_at, last_run_status, last_run_error, metadata_json, conversation_id, delete_after_run
            FROM cron_jobs
            WHERE $agentId IS NULL OR agent_id = $agentId
            ORDER BY created_at DESC
            """;
        command.Parameters.AddWithValue("$agentId", agentId.HasValue ? (object)agentId.Value.Value : DBNull.Value);

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
                    id, name, schedule, action_type, agent_id, message, template_name, template_parameters_json, model, webhook_url, shell_command,
                    enabled, system, time_zone, created_by, created_at, last_run_at, next_run_at, last_run_status, last_run_error, metadata_json, conversation_id, delete_after_run
                )
                VALUES (
                    $id, $name, $schedule, $actionType, $agentId, $message, @templateName, @templateParametersJson, $model, $webhookUrl, $shellCommand,
                    $enabled, $system, $timeZone, $createdBy, $createdAt, $lastRunAt, $nextRunAt, $lastRunStatus, $lastRunError, $metadataJson, $conversationId, $deleteAfterRun
                )
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    schedule = excluded.schedule,
                    action_type = excluded.action_type,
                    agent_id = excluded.agent_id,
                    message = excluded.message,
                    template_name = excluded.template_name,
                    template_parameters_json = excluded.template_parameters_json,
                    model = excluded.model,
                    webhook_url = excluded.webhook_url,
                    shell_command = excluded.shell_command,
                    enabled = excluded.enabled,
                    system = excluded.system,
                    time_zone = excluded.time_zone,
                    created_by = excluded.created_by,
                    created_at = excluded.created_at,
                    last_run_at = excluded.last_run_at,
                    next_run_at = excluded.next_run_at,
                    last_run_status = excluded.last_run_status,
                    last_run_error = excluded.last_run_error,
                    metadata_json = excluded.metadata_json,
                    conversation_id = excluded.conversation_id,
                    delete_after_run = excluded.delete_after_run
                """;
            BindJob(command, job);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Updated cron job '{JobId}' (action={ActionType}, enabled={Enabled}).",
                job.Id,
                job.ActionType,
                job.Enabled);
            return job;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically stamps <paramref name="conversationId"/> onto the job ONLY if the job's
    /// <c>conversation_id</c> column is currently NULL. Returns the winning conversation
    /// id (which may be a value pinned by a concurrent run). Returns <c>null</c> if the
    /// job no longer exists.
    /// </summary>
    /// <remarks>
    /// This is the CAS primitive that prevents the first-run race: two concurrent runs of
    /// the same cron job can both create a Conversation, but only one wins the stamp. The
    /// loser archives its own conversation and falls back to the winner. See
    /// <see cref="CronScheduler.RunActionAsync"/> for the consumer.
    /// </remarks>
    public async Task<ConversationId?> TrySetConversationIdAsync(
        JobId jobId,
        ConversationId conversationId,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var cas = connection.CreateCommand();
            cas.CommandText = """
                UPDATE cron_jobs
                SET conversation_id = $conversationId
                WHERE id = $jobId AND conversation_id IS NULL
                """;
            cas.Parameters.AddWithValue("$conversationId", conversationId.Value);
            cas.Parameters.AddWithValue("$jobId", jobId.Value);
            await cas.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT conversation_id FROM cron_jobs WHERE id = $jobId";
            read.Parameters.AddWithValue("$jobId", jobId.Value);
            var result = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (result is null or DBNull)
                return null;

            return ConversationId.From((string)result);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsync(JobId jobId, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var deleteRuns = connection.CreateCommand();
            deleteRuns.CommandText = "DELETE FROM cron_runs WHERE job_id = $jobId";
            deleteRuns.Parameters.AddWithValue("$jobId", jobId.Value);
            await deleteRuns.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var deleteJob = connection.CreateCommand();
            deleteJob.CommandText = "DELETE FROM cron_jobs WHERE id = $jobId";
            deleteJob.Parameters.AddWithValue("$jobId", jobId.Value);
            await deleteJob.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Deleted cron job '{JobId}'.", jobId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var run = new CronRun
            {
                Id = RunId.Create(),
                JobId = jobId,
                StartedAt = now,
                Status = CronRunStatus.Running
            };

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var insertRun = connection.CreateCommand();
            insertRun.CommandText = """
                INSERT INTO cron_runs (id, job_id, started_at, completed_at, status, error, session_id)
                VALUES ($id, $jobId, $startedAt, $completedAt, $status, $error, $sessionId)
                """;
            insertRun.Parameters.AddWithValue("$id", run.Id.Value);
            insertRun.Parameters.AddWithValue("$jobId", run.JobId.Value);
            insertRun.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
            insertRun.Parameters.AddWithValue("$completedAt", DBNull.Value);
            insertRun.Parameters.AddWithValue("$status", run.Status);
            insertRun.Parameters.AddWithValue("$error", DBNull.Value);
            insertRun.Parameters.AddWithValue("$sessionId", DBNull.Value);
            await insertRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var updateJob = connection.CreateCommand();
            updateJob.CommandText = """
                UPDATE cron_jobs
                SET last_run_status = $status,
                    last_run_error = NULL,
                    last_run_at = $lastRunAt
                WHERE id = $jobId
                """;
            updateJob.Parameters.AddWithValue("$status", run.Status);
            updateJob.Parameters.AddWithValue("$lastRunAt", now.ToString("O"));
            updateJob.Parameters.AddWithValue("$jobId", jobId.Value);
            await updateJob.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return run;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordRunCompleteAsync(
        RunId runId,
        string status,
        string? error = null,
        SessionId? sessionId = null,
        CancellationToken ct = default)
    {
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
            command.Parameters.AddWithValue("$sessionId", sessionId.HasValue ? (object)sessionId.Value.Value : DBNull.Value);
            command.Parameters.AddWithValue("$runId", runId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(JobId jobId, int limit = 20, CancellationToken ct = default)
    {
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
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$limit", cappedLimit);

        List<CronRun> runs = [];
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            runs.Add(ReadRun(reader));

        return runs;
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        // busy_timeout is per-connection and resets to 0 on every open, so it must be applied on
        // EVERY connection (operations open a fresh connection each time) - not just at init like
        // the database-level journal_mode=WAL. Without it a concurrent cross-process writer hits
        // SQLITE_BUSY immediately instead of waiting briefly for the lock to clear (#1450).
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Open)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }
        };
        return connection;
    }

    private static void BindJob(SqliteCommand command, CronJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id.Value);
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$schedule", job.Schedule);
        command.Parameters.AddWithValue("$actionType", job.ActionType);
        command.Parameters.AddWithValue("$agentId", job.AgentId.HasValue ? (object)job.AgentId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)job.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("@templateName", (object?)job.TemplateName ?? DBNull.Value);
        command.Parameters.AddWithValue("@templateParametersJson", SerializeTemplateParameters(job.TemplateParameters));
        command.Parameters.AddWithValue("$model", (object?)job.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$webhookUrl", (object?)job.WebhookUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$shellCommand", (object?)job.ShellCommand ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", job.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$system", job.System ? 1 : 0);
        command.Parameters.AddWithValue("$timeZone", (object?)job.TimeZone ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdBy", (object?)job.CreatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastRunAt", ToNullableString(job.LastRunAt));
        command.Parameters.AddWithValue("$nextRunAt", ToNullableString(job.NextRunAt));
        command.Parameters.AddWithValue("$lastRunStatus", (object?)job.LastRunStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastRunError", (object?)job.LastRunError ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", SerializeMetadata(job.Metadata));
        command.Parameters.AddWithValue("$conversationId", job.ConversationId.HasValue ? (object)job.ConversationId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("$deleteAfterRun", job.DeleteAfterRun ? 1 : 0);
    }

    private static CronJob ReadJob(SqliteDataReader reader)
    {
        var templateParametersJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        var metadataJson = reader.IsDBNull(20) ? null : reader.GetString(20);
        return new CronJob
        {
            Id = JobId.From(reader.GetString(0)),
            Name = reader.GetString(1),
            Schedule = reader.GetString(2),
            ActionType = reader.GetString(3),
            AgentId = reader.IsDBNull(4) ? null : AgentId.From(reader.GetString(4)),
            Message = reader.IsDBNull(5) ? null : reader.GetString(5),
            TemplateName = reader.IsDBNull(6) ? null : reader.GetString(6),
            TemplateParameters = DeserializeTemplateParameters(templateParametersJson),
            Model = reader.IsDBNull(8) ? null : reader.GetString(8),
            WebhookUrl = reader.IsDBNull(9) ? null : reader.GetString(9),
            ShellCommand = reader.IsDBNull(10) ? null : reader.GetString(10),
            Enabled = !reader.IsDBNull(11) && reader.GetInt32(11) != 0,
            System = !reader.IsDBNull(12) && reader.GetInt32(12) != 0,
            TimeZone = reader.IsDBNull(13) ? null : reader.GetString(13),
            CreatedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
            CreatedAt = ParseDate(reader.GetString(15)),
            LastRunAt = reader.IsDBNull(16) ? null : ParseDate(reader.GetString(16)),
            NextRunAt = reader.IsDBNull(17) ? null : ParseDate(reader.GetString(17)),
            LastRunStatus = reader.IsDBNull(18) ? null : reader.GetString(18),
            LastRunError = reader.IsDBNull(19) ? null : reader.GetString(19),
            Metadata = DeserializeMetadata(metadataJson),
            ConversationId = reader.IsDBNull(21) ? null : ConversationId.From(reader.GetString(21)),
            DeleteAfterRun = !reader.IsDBNull(22) && reader.GetInt32(22) != 0
        };
    }

    private static CronRun ReadRun(SqliteDataReader reader)
    {
        return new CronRun
        {
            Id = RunId.From(reader.GetString(0)),
            JobId = JobId.From(reader.GetString(1)),
            StartedAt = ParseDate(reader.GetString(2)),
            CompletedAt = reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
            Status = reader.GetString(4),
            Error = reader.IsDBNull(5) ? null : reader.GetString(5),
            SessionId = reader.IsDBNull(6) ? null : SessionId.From(reader.GetString(6))
        };
    }

    private static object ToNullableString(DateTimeOffset? value)
        => value is null ? DBNull.Value : value.Value.ToString("O");

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;

    private static object SerializeMetadata(IReadOnlyDictionary<string, object?>? metadata)
        => metadata is null ? DBNull.Value : JsonSerializer.Serialize(metadata, JsonOptions);

    private static object SerializeTemplateParameters(IReadOnlyDictionary<string, string?>? parameters)
        => parameters is null ? DBNull.Value : JsonSerializer.Serialize(parameters, JsonOptions);

    private static IReadOnlyDictionary<string, object?>? DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, JsonOptions);
    }

    private static IReadOnlyDictionary<string, string?>? DeserializeTemplateParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, string?>>(parametersJson, JsonOptions);
    }

    /// <inheritdoc />
    public async Task<int> PurgeRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM cron_runs
            WHERE completed_at IS NOT NULL
              AND completed_at < $cutoff
              AND status IN ('completed', 'failed')
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        var deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogDebug("Purged {Count} cron run record(s) older than {Cutoff}.", deleted, cutoff);
        return deleted;
    }
}
