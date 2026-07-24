using System.Text.Json;
using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BotNexus.Persistence.Sqlite;

namespace BotNexus.Cron;

public sealed class SqliteCronStore(string dbPath, IFileSystem? fileSystem = null, ILogger<SqliteCronStore>? logger = null) : ICronStore
{
    private readonly string _dbPath = dbPath;
    private readonly SqliteWalMaintenance _walMaintenance = new(fileSystem);
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

            // #1436: filesystem-aware journal mode (WAL on local disk, DELETE on network
            // mounts) with bounded wal_autocheckpoint, consolidated into the shared helper.
            await _walMaintenance.ApplyJournalModeAsync(connection, _dbPath, cancellationToken: ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
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

    /// <summary>
    /// Applies a user-owned <b>definition</b> update to an existing job. Writes only the
    /// caller-authored columns (name, schedule, action, agent, message, template, model,
    /// webhook, shell, enabled, system, time zone, delete-after-run, created-by, metadata).
    /// It deliberately does <b>not</b> touch scheduler-owned runtime bookkeeping
    /// (<c>last_run_*</c>, <c>next_run_at</c>) or the CAS-established <c>conversation_id</c>,
    /// so a controller/tool edit racing a concurrent run can never regress run status,
    /// timestamps, next-run, or the pinned conversation (#2133). Rescheduling after a
    /// schedule change is a separate <see cref="SetNextRunAtAsync"/> call. Returns the
    /// re-read job (runtime columns reflect the current stored state), or <c>null</c> if the
    /// job no longer exists.
    /// </summary>
    public async Task<CronJob?> UpdateDefinitionAsync(CronJob job, CancellationToken ct = default)
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
                UPDATE cron_jobs SET
                    name = $name,
                    schedule = $schedule,
                    action_type = $actionType,
                    agent_id = $agentId,
                    message = $message,
                    template_name = @templateName,
                    template_parameters_json = @templateParametersJson,
                    model = $model,
                    webhook_url = $webhookUrl,
                    shell_command = $shellCommand,
                    enabled = $enabled,
                    system = $system,
                    time_zone = $timeZone,
                    created_by = $createdBy,
                    delete_after_run = $deleteAfterRun,
                    metadata_json = $metadataJson
                WHERE id = $id
                """;
            BindDefinition(command, job);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Updated cron job definition '{JobId}' (action={ActionType}, enabled={Enabled}).",
                job.Id,
                job.ActionType,
                job.Enabled);
        }
        finally
        {
            _writeLock.Release();
        }

        return await GetAsync(job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Scheduler-owned narrow write of <c>next_run_at</c> only. Used by the scheduler's
    /// Phase-1 initialization/stale-correction and its Phase-2 post-run reschedule, and by
    /// controller/tool definition edits that explicitly change the schedule. It never touches
    /// any definition column, the <c>last_run_*</c> bookkeeping, or the conversation pin, so a
    /// reschedule racing a concurrent definition edit cannot clobber the edit (#2133).
    /// </summary>
    public async Task SetNextRunAtAsync(JobId jobId, DateTimeOffset? nextRunAt, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE cron_jobs
                SET next_run_at = $nextRunAt
                WHERE id = $jobId
                """;
            command.Parameters.AddWithValue("$nextRunAt", ToNullableString(nextRunAt));
            command.Parameters.AddWithValue("$jobId", jobId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Scheduler-owned narrow write of the terminal run bookkeeping (<c>last_run_at</c>,
    /// <c>last_run_status</c>, <c>last_run_error</c>) for a completed run. It never touches any
    /// definition column, <c>next_run_at</c>, or the conversation pin, so run finalization racing
    /// a concurrent definition edit cannot overwrite the edit, and cannot regress the next-run or
    /// conversation state (#2133).
    /// </summary>
    public async Task RecordRunFinalizationAsync(
        JobId jobId,
        DateTimeOffset lastRunAt,
        string lastRunStatus,
        string? lastRunError,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastRunStatus);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE cron_jobs
                SET last_run_at = $lastRunAt,
                    last_run_status = $lastRunStatus,
                    last_run_error = $lastRunError
                WHERE id = $jobId
                """;
            command.Parameters.AddWithValue("$lastRunAt", lastRunAt.ToString("O"));
            command.Parameters.AddWithValue("$lastRunStatus", lastRunStatus);
            command.Parameters.AddWithValue("$lastRunError", (object?)lastRunError ?? DBNull.Value);
            command.Parameters.AddWithValue("$jobId", jobId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        => SqliteConnectionFactory.Create(_connectionString);

    // #2133: binds ONLY the user-owned definition columns for the narrow UpdateDefinitionAsync
    // write. Excludes created_at (immutable after create), last_run_*/next_run_at (scheduler-owned)
    // and conversation_id (CAS-owned) so a definition edit cannot regress runtime/conversation state.
    private static void BindDefinition(SqliteCommand command, CronJob job)
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
        command.Parameters.AddWithValue("$deleteAfterRun", job.DeleteAfterRun ? 1 : 0);
        command.Parameters.AddWithValue("$metadataJson", SerializeMetadata(job.Metadata));
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

    private CronJob ReadJob(SqliteDataReader reader)
    {
        var jobId = JobId.From(reader.GetString(0));
        var templateParametersJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        var metadataJson = reader.IsDBNull(20) ? null : reader.GetString(20);
        return new CronJob
        {
            Id = jobId,
            Name = reader.GetString(1),
            Schedule = reader.GetString(2),
            ActionType = reader.GetString(3),
            AgentId = reader.IsDBNull(4) ? null : AgentId.From(reader.GetString(4)),
            Message = reader.IsDBNull(5) ? null : reader.GetString(5),
            TemplateName = reader.IsDBNull(6) ? null : reader.GetString(6),
            TemplateParameters = DeserializeTemplateParameters(templateParametersJson, jobId),
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
            Metadata = DeserializeMetadata(metadataJson, jobId),
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

    // #1751: guard stored-column JSON reads. A single corrupt metadata_json / template_parameters_json
    // value must not throw JsonException out of ReadJob and abort the whole ListAsync/GetAsync scan
    // (that would leave the scheduler unable to enumerate ANY jobs). On parse failure we log a warning
    // with the job id and degrade the property to null - the columns are already nullable, so the job
    // still loads with null metadata rather than poisoning the entire load.
    private IReadOnlyDictionary<string, object?>? DeserializeMetadata(string? metadataJson, JobId jobId)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping corrupt metadata_json for cron job '{JobId}'; the job will load with null metadata.",
                jobId.Value);
            return null;
        }
    }

    private IReadOnlyDictionary<string, string?>? DeserializeTemplateParameters(string? parametersJson, JobId jobId)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(parametersJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping corrupt template_parameters_json for cron job '{JobId}'; the job will load with null template parameters.",
                jobId.Value);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> PurgeRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Only terminal runs are purged, and only the statuses the scheduler actually writes
        // (ok / error / timed_out). These are bound from the CronRunStatus constants so the
        // filter cannot drift from the producers. In-flight 'running' runs (and any other
        // non-terminal status) are never deleted; completed_at < cutoff enforces retention.
        command.CommandText = """
            DELETE FROM cron_runs
            WHERE completed_at IS NOT NULL
              AND completed_at < $cutoff
              AND status IN ($statusOk, $statusError, $statusTimedOut)
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        command.Parameters.AddWithValue("$statusOk", CronRunStatus.Ok);
        command.Parameters.AddWithValue("$statusError", CronRunStatus.Error);
        command.Parameters.AddWithValue("$statusTimedOut", CronRunStatus.TimedOut);
        var deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogDebug("Purged {Count} cron run record(s) older than {Cutoff}.", deleted, cutoff);
        return deleted;
    }
}
