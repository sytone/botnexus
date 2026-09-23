using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Persists the standalone managed-task state machine as fenced projections and append-only facts.
/// The ledger owns each write transaction so an accepted command cannot expose only half of its state.
/// </summary>
public sealed class SqliteManagedTaskFlowLedger : IAsyncDisposable
{
    /// <summary>The first independently versioned schema understood by this ledger.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _connection;
    private readonly IManagedTaskFlowCommitObserver? _observer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Opens or creates a managed-task ledger at <paramref name="databasePath"/>.</summary>
    public SqliteManagedTaskFlowLedger(string databasePath, IManagedTaskFlowCommitObserver? observer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _observer = observer;
        _connection = SqliteConnectionFactory.Create($"Data Source={databasePath}");
        _connection.Open();
        ExecutePragma("PRAGMA foreign_keys=ON;");
        InitializeSchema();
    }

    internal bool ForeignKeysEnabled
    {
        get
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
    }

    /// <summary>Creates a run exactly once while preserving its specification byte-for-byte as JSON.</summary>
    public Task<ManagedTaskLedgerWriteResult> CreateRunAsync(
        CreateManagedTaskRunCommand command,
        CancellationToken cancellationToken = default)
    {
        var identity = CommandIdentity.Create("create-run", command.Specification.RunId, command.Specification);
        return WriteAsync(command.CommandId, identity, (transaction, now) =>
        {
            var duplicate = ReadDuplicate(command.CommandId, identity, transaction);
            if (duplicate is not null)
                return duplicate;

            var existing = ReadSnapshot(command.Specification.RunId, transaction);
            if (existing is not null)
            {
                var detail = existing.Run.Specification != command.Specification
                    ? "a different immutable specification"
                    : "another command";
                throw new ManagedTaskLedgerConflictException(
                    $"Run '{command.Specification.RunId}' already exists with {detail}.");
            }

            Execute(transaction,
                "INSERT INTO managed_task_run(run_id, specification_json, status, revision, created_at, updated_at) " +
                "VALUES($run, $specification, $status, 0, $now, $now);",
                ("$run", command.Specification.RunId), ("$specification", Serialize(command.Specification)),
                ("$status", ManagedTaskRunStatus.Pending.ToString()), ("$now", Format(now)));

            foreach (var step in command.Specification.Steps)
            {
                Execute(transaction,
                    "INSERT INTO managed_task_step(run_id, step_id, specification_json, revision, created_at, updated_at) " +
                    "VALUES($run, $step, $specification, 0, $now, $now);",
                    ("$run", command.Specification.RunId), ("$step", step.StepId),
                    ("$specification", Serialize(step)), ("$now", Format(now)));
            }

            AppendEvent(transaction, command.Specification.RunId, command.CommandId, ManagedTaskEventType.RunCreated, now);
            return Applied(command.CommandId, identity, transaction, now);
        }, cancellationToken);
    }

    /// <summary>Transitions a run under an expected-revision fence.</summary>
    public Task<ManagedTaskLedgerWriteResult> TransitionRunAsync(
        TransitionManagedTaskRunCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.Status == ManagedTaskRunStatus.Cancelled)
            throw new ArgumentException("Cancellation must use CancelRunAsync so active attempts are cancelled atomically.", nameof(command));

        var identity = CommandIdentity.Create("transition-run", command.RunId, new
        {
            command.ExpectedRevision,
            command.Status,
        });
        return WriteAsync(command.CommandId, identity, (transaction, now) =>
        {
            var duplicate = ReadDuplicate(command.CommandId, identity, transaction);
            if (duplicate is not null)
                return duplicate;

            var snapshot = ReadSnapshot(command.RunId, transaction);
            if (snapshot is null)
                return NotFound();
            if (snapshot.Run.Status == ManagedTaskRunStatus.Cancelled)
                return Result(ManagedTaskLedgerWriteOutcome.Cancelled, snapshot);
            if (IsTerminal(snapshot.Run.Status))
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
            if (snapshot.Run.Revision != command.ExpectedRevision)
                return Result(ManagedTaskLedgerWriteOutcome.RevisionConflict, snapshot);
            if (!IsValidTransition(snapshot.Run.Status, command.Status))
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);


            var updated = Execute(transaction,
                "UPDATE managed_task_run SET status=$status, revision=revision+1, updated_at=$now " +
                "WHERE run_id=$run AND revision=$revision AND status=$current;",
                ("$status", command.Status.ToString()), ("$now", Format(now)), ("$run", command.RunId),
                ("$revision", command.ExpectedRevision), ("$current", snapshot.Run.Status.ToString()));
            if (updated != 1)
                return Result(ManagedTaskLedgerWriteOutcome.RevisionConflict, RequireSnapshot(command.RunId, transaction));

            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.RunTransitioned, now);
            return Applied(command.CommandId, identity, transaction, now);
        }, cancellationToken);
    }

    /// <summary>Admits an attempt with the next durable per-step epoch.</summary>
    public Task<ManagedTaskLedgerWriteResult> AdmitAttemptAsync(
        AdmitManagedTaskAttemptCommand command,
        CancellationToken cancellationToken = default)
    {
        var identity = CommandIdentity.Create("admit-attempt", command.RunId, new
        {
            command.StepId,
            command.ExpectedStepRevision,
            command.AttemptId,
        });
        return WriteAsync(command.CommandId, identity, (transaction, now) =>
        {
            var duplicate = ReadDuplicate(command.CommandId, identity, transaction);
            if (duplicate is not null)
                return duplicate;

            var snapshot = ReadSnapshot(command.RunId, transaction);
            if (snapshot is null)
                return NotFound();
            if (snapshot.Run.Status == ManagedTaskRunStatus.Cancelled)
                return Result(ManagedTaskLedgerWriteOutcome.Cancelled, snapshot);
            if (IsTerminal(snapshot.Run.Status))
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
            var step = snapshot.Steps.SingleOrDefault(item => item.StepId == command.StepId);
            if (step is null)
                return Result(ManagedTaskLedgerWriteOutcome.StepNotFound, snapshot);
            if (step.Revision != command.ExpectedStepRevision)
                return Result(ManagedTaskLedgerWriteOutcome.RevisionConflict, snapshot);
            if (snapshot.Attempts.Any(item => item.StepId == command.StepId && !item.IsRetryable))
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);

            var epoch = snapshot.Attempts.Where(item => item.StepId == command.StepId)
                .Select(item => item.Epoch).DefaultIfEmpty(0).Max() + 1;
            Execute(transaction,
                "INSERT INTO managed_task_attempt(run_id, step_id, attempt_id, epoch, status, created_at, updated_at) " +
                "VALUES($run, $step, $attempt, $epoch, $status, $now, $now);",
                ("$run", command.RunId), ("$step", command.StepId), ("$attempt", command.AttemptId),
                ("$epoch", epoch), ("$status", ManagedTaskAttemptStatus.Running.ToString()), ("$now", Format(now)));
            var updated = Execute(transaction,
                "UPDATE managed_task_step SET revision=revision+1, updated_at=$now " +
                "WHERE run_id=$run AND step_id=$step AND revision=$revision;",
                ("$now", Format(now)), ("$run", command.RunId), ("$step", command.StepId),
                ("$revision", command.ExpectedStepRevision));
            if (updated != 1)
                return Result(ManagedTaskLedgerWriteOutcome.RevisionConflict, RequireSnapshot(command.RunId, transaction));

            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.AttemptAdmitted, now);
            return Applied(command.CommandId, identity, transaction, now);
        }, cancellationToken);
    }

    /// <summary>Commits a terminal attempt result under step-revision and attempt-epoch fences.</summary>
    public Task<ManagedTaskLedgerWriteResult> CommitAttemptAsync(
        CommitManagedTaskAttemptCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.Status == ManagedTaskAttemptStatus.Running)
            throw new ArgumentException("An attempt commit must be terminal.", nameof(command));

        var identity = CommandIdentity.Create("commit-attempt", command.RunId, new
        {
            command.StepId,
            command.AttemptId,
            command.ExpectedStepRevision,
            command.ExpectedAttemptEpoch,
            command.Status,
            command.ResultReference,
        });
        return WriteAsync(command.CommandId, identity, (transaction, now) =>
        {
            var duplicate = ReadDuplicate(command.CommandId, identity, transaction);
            if (duplicate is not null)
                return duplicate;

            var snapshot = ReadSnapshot(command.RunId, transaction);
            if (snapshot is null)
                return NotFound();
            if (snapshot.Run.Status == ManagedTaskRunStatus.Cancelled)
                return Result(ManagedTaskLedgerWriteOutcome.Cancelled, snapshot);
            if (IsTerminal(snapshot.Run.Status))
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
            var step = snapshot.Steps.SingleOrDefault(item => item.StepId == command.StepId);
            if (step is null)
                return Result(ManagedTaskLedgerWriteOutcome.StepNotFound, snapshot);
            var attempt = snapshot.Attempts.SingleOrDefault(item =>
                item.StepId == command.StepId && item.AttemptId == command.AttemptId);
            if (attempt is null)
                return Result(ManagedTaskLedgerWriteOutcome.AttemptNotFound, snapshot);
            if (attempt.Epoch != command.ExpectedAttemptEpoch)
                return Result(ManagedTaskLedgerWriteOutcome.EpochConflict, snapshot);
            if (step.Revision != command.ExpectedStepRevision)
                return Result(ManagedTaskLedgerWriteOutcome.RevisionConflict, snapshot);
            if (attempt.IsTerminal)
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);

            var attemptUpdated = Execute(transaction,
                "UPDATE managed_task_attempt SET status=$status, result_reference=$result, updated_at=$now " +
                "WHERE run_id=$run AND step_id=$step AND attempt_id=$attempt AND epoch=$epoch AND status=$running;",
                ("$status", command.Status.ToString()), ("$result", command.ResultReference), ("$now", Format(now)),
                ("$run", command.RunId), ("$step", command.StepId), ("$attempt", command.AttemptId),
                ("$epoch", command.ExpectedAttemptEpoch), ("$running", ManagedTaskAttemptStatus.Running.ToString()));
            if (attemptUpdated != 1)
                return Result(ManagedTaskLedgerWriteOutcome.EpochConflict, RequireSnapshot(command.RunId, transaction));

            var stepUpdated = Execute(transaction,
                "UPDATE managed_task_step SET revision=revision+1, updated_at=$now " +
                "WHERE run_id=$run AND step_id=$step AND revision=$revision;",
                ("$now", Format(now)), ("$run", command.RunId), ("$step", command.StepId),
                ("$revision", command.ExpectedStepRevision));
            if (stepUpdated != 1)
                return Result(ManagedTaskLedgerWriteOutcome.RevisionConflict, RequireSnapshot(command.RunId, transaction));

            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.AttemptCommitted, now);
            return Applied(command.CommandId, identity, transaction, now);
        }, cancellationToken);
    }

    /// <summary>Cancels a run and every active attempt in the same durable transaction.</summary>
    public Task<ManagedTaskLedgerWriteResult> CancelRunAsync(
        CancelManagedTaskRunCommand command,
        CancellationToken cancellationToken = default)
    {
        var identity = CommandIdentity.Create("cancel-run", command.RunId, new
        {
            command.ExpectedRunRevision,
            command.Reason,
        });
        return WriteAsync(command.CommandId, identity, (transaction, now) =>
        {
            var duplicate = ReadDuplicate(command.CommandId, identity, transaction);
            if (duplicate is not null)
                return duplicate;

            var snapshot = ReadSnapshot(command.RunId, transaction);
            if (snapshot is null)
                return NotFound();
            if (snapshot.Run.Status == ManagedTaskRunStatus.Cancelled)
                return Result(ManagedTaskLedgerWriteOutcome.Cancelled, snapshot);
            if (IsTerminal(snapshot.Run.Status))
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
            if (snapshot.Run.Revision != command.ExpectedRunRevision)
                return Result(ManagedTaskLedgerWriteOutcome.RevisionConflict, snapshot);

            var updated = Execute(transaction,
                "UPDATE managed_task_run SET status=$status, revision=revision+1, updated_at=$now, cancellation_reason=$reason " +
                "WHERE run_id=$run AND revision=$revision AND status IN ($pending, $running);",
                ("$status", ManagedTaskRunStatus.Cancelled.ToString()), ("$now", Format(now)),
                ("$reason", command.Reason), ("$run", command.RunId), ("$revision", command.ExpectedRunRevision),
                ("$pending", ManagedTaskRunStatus.Pending.ToString()), ("$running", ManagedTaskRunStatus.Running.ToString()));
            if (updated != 1)
                return Result(ManagedTaskLedgerWriteOutcome.RevisionConflict, RequireSnapshot(command.RunId, transaction));

            Execute(transaction,
                "UPDATE managed_task_attempt SET status=$status, updated_at=$now WHERE run_id=$run AND status=$running;",
                ("$status", ManagedTaskAttemptStatus.Cancelled.ToString()), ("$now", Format(now)),
                ("$run", command.RunId), ("$running", ManagedTaskAttemptStatus.Running.ToString()));
            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.RunCancelled, now);
            return Applied(command.CommandId, identity, transaction, now);
        }, cancellationToken);
    }

    /// <summary>Reads a consistent snapshot of a run, or <see langword="null"/> when it is absent.</summary>
    public async Task<ManagedTaskRunSnapshot?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ReadSnapshot(runId); }
        finally { _gate.Release(); }
    }

    /// <summary>Reads a run's facts in durable insertion order.</summary>
    public async Task<IReadOnlyList<ManagedTaskEventRecord>> GetEventsAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT sequence, run_id, command_id, type, occurred_at FROM managed_task_event " +
                "WHERE run_id=$run ORDER BY sequence;";
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            var events = new List<ManagedTaskEventRecord>();
            while (reader.Read())
            {
                events.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    Enum.Parse<ManagedTaskEventType>(reader.GetString(3)), Parse(reader.GetString(4))));
            }
            return events;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Closes the ledger connection after all in-process operations finish.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private async Task<ManagedTaskLedgerWriteResult> WriteAsync(
        string commandId,
        CommandIdentity identity,
        Func<SqliteTransaction, DateTimeOffset, ManagedTaskLedgerWriteResult> body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.RunId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An immediate transaction obtains the writer reservation before any projection read.
            // Together with conditional UPDATE predicates this prevents a second ledger instance
            // from validating against one revision and later committing against another.
            using var transaction = _connection.BeginTransaction(deferred: false);
            var result = body(transaction, DateTimeOffset.UtcNow);
            if (result.Outcome != ManagedTaskLedgerWriteOutcome.Applied)
            {
                transaction.Rollback();
                return result;
            }

            _observer?.OnCommitPoint(ManagedTaskFlowCommitPoint.BeforeCommit, commandId);
            transaction.Commit();
            _observer?.OnCommitPoint(ManagedTaskFlowCommitPoint.AfterCommit, commandId);
            return result;
        }
        finally { _gate.Release(); }
    }

    private ManagedTaskLedgerWriteResult Applied(
        string commandId,
        CommandIdentity identity,
        SqliteTransaction transaction,
        DateTimeOffset now)
    {
        var snapshot = RequireSnapshot(identity.RunId, transaction);
        RecordCommand(transaction, commandId, identity, now);
        return Result(ManagedTaskLedgerWriteOutcome.Applied, snapshot);
    }

    private void InitializeSchema()
    {
        using var version = _connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var stored = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (stored > CurrentSchemaVersion)
            throw new InvalidOperationException($"Managed task ledger schema {stored} is newer than supported version {CurrentSchemaVersion}.");

        using var transaction = _connection.BeginTransaction(deferred: false);
        Execute(transaction, """
            CREATE TABLE IF NOT EXISTS managed_task_run(
                run_id TEXT PRIMARY KEY,
                specification_json TEXT NOT NULL,
                status TEXT NOT NULL,
                revision INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                cancellation_reason TEXT NULL);
            CREATE TABLE IF NOT EXISTS managed_task_step(
                run_id TEXT NOT NULL,
                step_id TEXT NOT NULL,
                specification_json TEXT NOT NULL,
                revision INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(run_id, step_id),
                FOREIGN KEY(run_id) REFERENCES managed_task_run(run_id));
            CREATE TABLE IF NOT EXISTS managed_task_attempt(
                run_id TEXT NOT NULL,
                step_id TEXT NOT NULL,
                attempt_id TEXT NOT NULL,
                epoch INTEGER NOT NULL,
                status TEXT NOT NULL,
                result_reference TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(run_id, step_id, attempt_id),
                UNIQUE(run_id, step_id, epoch),
                FOREIGN KEY(run_id, step_id) REFERENCES managed_task_step(run_id, step_id));
            CREATE TABLE IF NOT EXISTS managed_task_event(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                command_id TEXT NOT NULL UNIQUE,
                type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                FOREIGN KEY(run_id) REFERENCES managed_task_run(run_id));
            CREATE TABLE IF NOT EXISTS managed_task_command(
                command_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                command_kind TEXT NOT NULL,
                payload_fingerprint TEXT NOT NULL,
                completed_at TEXT NOT NULL,
                FOREIGN KEY(run_id) REFERENCES managed_task_run(run_id));
            """);
        Execute(transaction, $"PRAGMA user_version = {CurrentSchemaVersion};");
        transaction.Commit();
    }

    private ManagedTaskLedgerWriteResult? ReadDuplicate(
        string commandId,
        CommandIdentity identity,
        SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT run_id, command_kind, payload_fingerprint FROM managed_task_command WHERE command_id=$command;";
        command.Parameters.AddWithValue("$command", commandId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var storedRunId = reader.GetString(0);
        var storedKind = reader.GetString(1);
        var storedFingerprint = reader.GetString(2);
        if (storedRunId != identity.RunId || storedKind != identity.Kind || storedFingerprint != identity.PayloadFingerprint)
        {
            throw new ManagedTaskLedgerConflictException(
                $"Command id '{commandId}' was already used for a different command or run.");
        }

        reader.Close();
        return Result(ManagedTaskLedgerWriteOutcome.Duplicate, RequireSnapshot(storedRunId, transaction));
    }

    private ManagedTaskRunSnapshot RequireSnapshot(string runId, SqliteTransaction transaction) =>
        ReadSnapshot(runId, transaction)
        ?? throw new InvalidOperationException($"Managed task run '{runId}' disappeared inside a write transaction.");

    private ManagedTaskRunSnapshot? ReadSnapshot(string runId, SqliteTransaction? transaction = null)
    {
        using var runCommand = _connection.CreateCommand();
        runCommand.Transaction = transaction;
        runCommand.CommandText =
            "SELECT specification_json, status, revision, created_at, updated_at, cancellation_reason " +
            "FROM managed_task_run WHERE run_id=$run;";
        runCommand.Parameters.AddWithValue("$run", runId);
        using var runReader = runCommand.ExecuteReader();
        if (!runReader.Read())
            return null;
        var specification = Deserialize<ManagedTaskRunSpecification>(runReader.GetString(0));
        var run = new ManagedTaskRunRecord(runId, specification,
            Enum.Parse<ManagedTaskRunStatus>(runReader.GetString(1)), runReader.GetInt64(2),
            Parse(runReader.GetString(3)), Parse(runReader.GetString(4)),
            runReader.IsDBNull(5) ? null : runReader.GetString(5));
        runReader.Close();

        var steps = new List<ManagedTaskStepRecord>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT step_id, specification_json, revision, created_at, updated_at " +
                "FROM managed_task_step WHERE run_id=$run ORDER BY rowid;";
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                steps.Add(new(runId, reader.GetString(0), Deserialize<ManagedTaskStepSpecification>(reader.GetString(1)),
                    reader.GetInt64(2), Parse(reader.GetString(3)), Parse(reader.GetString(4))));
            }
        }

        var attempts = new List<ManagedTaskAttemptRecord>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT step_id, attempt_id, epoch, status, result_reference, created_at, updated_at " +
                "FROM managed_task_attempt WHERE run_id=$run ORDER BY step_id, epoch;";
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                attempts.Add(new(runId, reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    Enum.Parse<ManagedTaskAttemptStatus>(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4), Parse(reader.GetString(5)), Parse(reader.GetString(6))));
            }
        }
        return new(run, new ManagedTaskValueList<ManagedTaskStepRecord>(steps),
            new ManagedTaskValueList<ManagedTaskAttemptRecord>(attempts));
    }

    private void RecordCommand(
        SqliteTransaction transaction,
        string commandId,
        CommandIdentity identity,
        DateTimeOffset now) =>
        Execute(transaction,
            "INSERT INTO managed_task_command(command_id, run_id, command_kind, payload_fingerprint, completed_at) " +
            "VALUES($command, $run, $kind, $fingerprint, $now);",
            ("$command", commandId), ("$run", identity.RunId), ("$kind", identity.Kind),
            ("$fingerprint", identity.PayloadFingerprint), ("$now", Format(now)));

    private void AppendEvent(
        SqliteTransaction transaction,
        string runId,
        string commandId,
        ManagedTaskEventType type,
        DateTimeOffset now) =>
        Execute(transaction,
            "INSERT INTO managed_task_event(run_id, command_id, type, occurred_at) VALUES($run, $command, $type, $now);",
            ("$run", runId), ("$command", commandId), ("$type", type.ToString()), ("$now", Format(now)));

    private int Execute(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private void ExecutePragma(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool IsTerminal(ManagedTaskRunStatus status) =>
        status is ManagedTaskRunStatus.Completed or ManagedTaskRunStatus.Failed or ManagedTaskRunStatus.Cancelled;

    private static bool IsValidTransition(ManagedTaskRunStatus current, ManagedTaskRunStatus target) =>
        (current, target) is
            (ManagedTaskRunStatus.Pending, ManagedTaskRunStatus.Running) or
            (ManagedTaskRunStatus.Running, ManagedTaskRunStatus.Completed) or
            (ManagedTaskRunStatus.Running, ManagedTaskRunStatus.Failed);

    private static ManagedTaskLedgerWriteResult NotFound() =>
        new(ManagedTaskLedgerWriteOutcome.RunNotFound, null);

    private static ManagedTaskLedgerWriteResult Result(
        ManagedTaskLedgerWriteOutcome outcome,
        ManagedTaskRunSnapshot snapshot) => new(outcome, snapshot);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidOperationException($"Stored {typeof(T).Name} JSON was null.");
    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed record CommandIdentity(string Kind, string RunId, string PayloadFingerprint)
    {
        public static CommandIdentity Create<T>(string kind, string runId, T payload)
        {
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
            return new(kind, runId, fingerprint);
        }
    }
}
