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
    public const int CurrentSchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _connection;
    private readonly IManagedTaskFlowCommitObserver? _observer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Opens or creates a managed-task ledger at <paramref name="databasePath"/>.</summary>
    public SqliteManagedTaskFlowLedger(
        string databasePath,
        IManagedTaskFlowCommitObserver? observer = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _observer = observer;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            if (IsTerminal(command.Status) && HasPendingWait(command.RunId, transaction))
                return Result(ManagedTaskLedgerWriteOutcome.PendingWaitConflict, snapshot);

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
            if (snapshot.Results.Any(item =>
                    item.StepId == command.StepId
                    && item.BusinessAcceptance == ManagedTaskResultAcceptance.Accepted))
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
            if (snapshot.Attempts.Any(item => item.StepId == command.StepId && !item.IsRetryable))
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
            if (HasPendingWait(command.RunId, command.StepId, transaction))
                return Result(ManagedTaskLedgerWriteOutcome.PendingWaitConflict, snapshot);

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
            Execute(transaction,
                "UPDATE managed_task_wait SET status=$status, revision=revision+1, updated_at=$now " +
                "WHERE run_id=$run AND status=$pending;",
                ("$status", ManagedTaskWaitStatus.Cancelled.ToString()), ("$now", Format(now)),
                ("$run", command.RunId), ("$pending", ManagedTaskWaitStatus.Pending.ToString()));
            Execute(transaction,
                "UPDATE managed_task_continuation_intent SET status=$status, generation=generation+1, updated_at=$now " +
                "WHERE run_id=$run AND status IN ($pending, $inProgress);",
                ("$status", ManagedTaskContinuationStatus.Cancelled.ToString()), ("$now", Format(now)),
                ("$run", command.RunId), ("$pending", ManagedTaskContinuationStatus.Pending.ToString()),
                ("$inProgress", ManagedTaskContinuationStatus.InProgress.ToString()));
            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.RunCancelled, now);
            return Applied(command.CommandId, identity, transaction, now);
        }, cancellationToken);
    }

    /// <summary>Records validation and acceptance, atomically enqueueing delivery only for accepted results.</summary>
    public Task<ManagedTaskLedgerWriteResult> RecordResultAsync(
        RecordManagedTaskResultCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.BusinessAcceptance == ManagedTaskResultAcceptance.Accepted)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command.CompletionId);
            if (command.SchemaValidation != ManagedTaskResultSchemaValidation.Valid)
                throw new ArgumentException("An accepted result must have valid schema.", nameof(command));
            if (command.MaxDeliveryAttempts < 1)
                throw new ArgumentOutOfRangeException(nameof(command), "Accepted results require at least one delivery attempt.");
        }
        else if (command.CompletionId is not null)
        {
            throw new ArgumentException("Rejected results cannot enqueue completion delivery.", nameof(command));
        }

        var identity = CommandIdentity.Create("record-result", command.RunId, command);
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
            if (!attempt.IsTerminal || attempt.Status == ManagedTaskAttemptStatus.Running)
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
            if (attempt.Status == ManagedTaskAttemptStatus.UnknownSideEffect)
                return Result(ManagedTaskLedgerWriteOutcome.ReconciliationRequired, snapshot);
            if (snapshot.Results.Any(item => item.StepId == command.StepId && item.AttemptId == command.AttemptId))
                return Result(ManagedTaskLedgerWriteOutcome.ResultAlreadyRecorded, snapshot);
            if (command.CompletionId is not null && snapshot.CompletionDeliveries.Any(item =>
                    item.CompletionId == command.CompletionId
                    && (item.StepId != command.StepId || item.AttemptId != command.AttemptId)))
                return Result(ManagedTaskLedgerWriteOutcome.LedgerConflict, snapshot);

            Execute(transaction, """
                INSERT INTO managed_task_result(
                    run_id, step_id, attempt_id, attempt_epoch, execution_outcome, result_reference,
                    schema_validation, business_acceptance, cleanup_status, recorded_at)
                VALUES($run, $step, $attempt, $epoch, $outcome, $reference, $schema, $acceptance, $cleanup, $now);
                """,
                ("$run", command.RunId), ("$step", command.StepId), ("$attempt", command.AttemptId),
                ("$epoch", command.ExpectedAttemptEpoch), ("$outcome", attempt.Status.ToString()),
                ("$reference", attempt.ResultReference), ("$schema", command.SchemaValidation.ToString()),
                ("$acceptance", command.BusinessAcceptance.ToString()),
                ("$cleanup", ManagedTaskResultCleanupStatus.Pending.ToString()), ("$now", Format(now)));
            if (command.BusinessAcceptance == ManagedTaskResultAcceptance.Accepted)
            {
                Execute(transaction, """
                    INSERT INTO managed_task_completion_delivery(
                        run_id, completion_id, step_id, attempt_id, status, generation, delivery_attempts,
                        max_delivery_attempts, last_failure, delivery_deadline, created_at, updated_at)
                    VALUES($run, $completion, $step, $attempt, $status, 0, 0, $max, NULL, $deadline, $now, $now);
                    """,
                    ("$run", command.RunId), ("$completion", command.CompletionId), ("$step", command.StepId),
                    ("$attempt", command.AttemptId), ("$status", ManagedTaskCompletionDeliveryStatus.Pending.ToString()),
                    ("$max", command.MaxDeliveryAttempts),
                    ("$deadline", command.DeliveryDeadline is null ? null : Format(command.DeliveryDeadline.Value)),
                    ("$now", Format(now)));
            }
            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.ResultRecorded, now);
            return Applied(command.CommandId, identity, transaction, now);
        }, cancellationToken);
    }

    /// <summary>Claims a completion intent once, incrementing its generation and bounded attempt count.</summary>
    public Task<ManagedTaskLedgerWriteResult> ClaimCompletionDeliveryAsync(
        ClaimManagedTaskCompletionDeliveryCommand command,
        CancellationToken cancellationToken = default) =>
        ChangeDeliveryAsync(command.CommandId, command.RunId, "claim-delivery", command, cancellationToken,
            (transaction, snapshot, delivery, now) =>
            {
                if (delivery.Generation != command.ExpectedGeneration)
                    return Result(ManagedTaskLedgerWriteOutcome.DeliveryGenerationConflict, snapshot);
                if (delivery.Status is not (ManagedTaskCompletionDeliveryStatus.Pending or ManagedTaskCompletionDeliveryStatus.Failed))
                    return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
                if (delivery.DeliveryDeadline is { } deadline && deadline <= now)
                {
                    UpdateDelivery(transaction, delivery, ManagedTaskCompletionDeliveryStatus.Discarded,
                        delivery.Generation + 1, delivery.DeliveryAttempts, "delivery deadline expired", now);
                    return AppliedDelivery(command.CommandId, command.RunId, command, transaction, now,
                        ManagedTaskLedgerWriteOutcome.Expired);
                }
                if (delivery.DeliveryAttempts >= delivery.MaxDeliveryAttempts)
                    return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
                UpdateDelivery(transaction, delivery, ManagedTaskCompletionDeliveryStatus.InProgress,
                    delivery.Generation + 1, delivery.DeliveryAttempts + 1, delivery.LastFailure, now);
                return AppliedDelivery(command.CommandId, command.RunId, command, transaction, now);
            });

    /// <summary>Requeues interrupted delivery after restart and advances the generation fence.</summary>
    public Task<ManagedTaskLedgerWriteResult> RecoverCompletionDeliveryAsync(
        RecoverManagedTaskCompletionDeliveryCommand command,
        CancellationToken cancellationToken = default) =>
        ChangeDeliveryAsync(command.CommandId, command.RunId, "recover-delivery", command, cancellationToken,
            (transaction, snapshot, delivery, now) =>
            {
                if (delivery.Generation != command.ExpectedGeneration)
                    return Result(ManagedTaskLedgerWriteOutcome.DeliveryGenerationConflict, snapshot);
                if (delivery.Status != ManagedTaskCompletionDeliveryStatus.InProgress)
                    return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
                var finalAttemptExhausted = delivery.DeliveryAttempts >= delivery.MaxDeliveryAttempts;
                var interruptionFailure = finalAttemptExhausted
                    ? "delivery interrupted while final allowed attempt was in progress"
                    : "delivery interrupted while attempt was in progress";
                var recoveredStatus = finalAttemptExhausted
                    ? ManagedTaskCompletionDeliveryStatus.Suspended
                    : ManagedTaskCompletionDeliveryStatus.Pending;
                UpdateDelivery(transaction, delivery, recoveredStatus,
                    delivery.Generation + 1, delivery.DeliveryAttempts, interruptionFailure, now);
                return AppliedDelivery(command.CommandId, command.RunId, command, transaction, now);
            });

    /// <summary>Commits delivered or failed requester notification under the claiming generation.</summary>
    public Task<ManagedTaskLedgerWriteResult> CompleteCompletionDeliveryAsync(
        CompleteManagedTaskCompletionDeliveryCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.Status is not (ManagedTaskCompletionDeliveryStatus.Delivered or ManagedTaskCompletionDeliveryStatus.Failed))
            throw new ArgumentException("Delivery completion must be delivered or failed.", nameof(command));
        if (command.Status == ManagedTaskCompletionDeliveryStatus.Failed && string.IsNullOrEmpty(command.LastFailure))
            throw new ArgumentException("Failed delivery requires the exact failure.", nameof(command));
        return ChangeDeliveryAsync(command.CommandId, command.RunId, "complete-delivery", command, cancellationToken,
            (transaction, snapshot, delivery, now) =>
            {
                if (delivery.Generation != command.ExpectedGeneration)
                    return Result(ManagedTaskLedgerWriteOutcome.DeliveryGenerationConflict, snapshot);
                if (delivery.Status != ManagedTaskCompletionDeliveryStatus.InProgress)
                    return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
                var status = command.Status == ManagedTaskCompletionDeliveryStatus.Failed
                    && delivery.DeliveryAttempts >= delivery.MaxDeliveryAttempts
                    ? ManagedTaskCompletionDeliveryStatus.Suspended
                    : command.Status;
                UpdateDelivery(transaction, delivery, status, delivery.Generation,
                    delivery.DeliveryAttempts, command.LastFailure, now);
                return AppliedDelivery(command.CommandId, command.RunId, command, transaction, now);
            });
    }

    /// <summary>Completes cleanup independently from execution, acceptance, and requester delivery.</summary>
    public Task<ManagedTaskLedgerWriteResult> CompleteResultCleanupAsync(
        CompleteManagedTaskResultCleanupCommand command,
        CancellationToken cancellationToken = default)
    {
        var identity = CommandIdentity.Create("complete-result-cleanup", command.RunId, command);
        return WriteAsync(command.CommandId, identity, (transaction, now) =>
        {
            var duplicate = ReadDuplicate(command.CommandId, identity, transaction);
            if (duplicate is not null)
                return duplicate;
            var snapshot = ReadSnapshot(command.RunId, transaction);
            if (snapshot is null)
                return NotFound();
            var result = snapshot.Results.SingleOrDefault(item =>
                item.StepId == command.StepId && item.AttemptId == command.AttemptId);
            if (result is null)
                return Result(ManagedTaskLedgerWriteOutcome.AttemptNotFound, snapshot);
            if (result.AttemptEpoch != command.ExpectedAttemptEpoch)
                return Result(ManagedTaskLedgerWriteOutcome.EpochConflict, snapshot);
            if (result.CleanupStatus == ManagedTaskResultCleanupStatus.Completed)
                return Result(ManagedTaskLedgerWriteOutcome.Terminal, snapshot);
            Execute(transaction, """
                UPDATE managed_task_result SET cleanup_status=$status
                WHERE run_id=$run AND step_id=$step AND attempt_id=$attempt AND attempt_epoch=$epoch;
                """, ("$status", ManagedTaskResultCleanupStatus.Completed.ToString()), ("$run", command.RunId),
                ("$step", command.StepId), ("$attempt", command.AttemptId),
                ("$epoch", command.ExpectedAttemptEpoch));
            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.ResultCleanupCompleted, now);
            return Applied(command.CommandId, identity, transaction, now);
        }, cancellationToken);
    }

    /// <summary>Parks a step with bounded durable resume metadata.</summary>
    public Task<ManagedTaskWaitWriteResult> ParkWaitAsync(
        ParkManagedTaskWaitCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateWaitMetadata(command);
        var identity = CommandIdentity.Create("park-wait", command.RunId, command);
        return WriteWaitAsync(command.CommandId, identity, (transaction, now) =>
        {
            var duplicate = ReadWaitDuplicate(command.CommandId, identity, command.WaitId, transaction);
            if (duplicate is not null)
                return duplicate;

            var snapshot = ReadSnapshot(command.RunId, transaction);
            if (snapshot is null)
                return new(ManagedTaskLedgerWriteOutcome.RunNotFound, null, null);
            if (snapshot.Run.Status == ManagedTaskRunStatus.Cancelled)
                return new(ManagedTaskLedgerWriteOutcome.Cancelled, null, null);
            if (IsTerminal(snapshot.Run.Status))
                return new(ManagedTaskLedgerWriteOutcome.Terminal, null, null);
            var step = snapshot.Steps.SingleOrDefault(item => item.StepId == command.StepId);
            if (step is null)
                return new(ManagedTaskLedgerWriteOutcome.StepNotFound, null, null);
            if (step.Revision != command.ExpectedStepRevision)
                return new(ManagedTaskLedgerWriteOutcome.RevisionConflict, null, null);
            if (HasPendingWait(command.RunId, command.StepId, transaction))
                return new(ManagedTaskLedgerWriteOutcome.PendingWaitConflict, null, null);

            var parkedStepRevision = command.ExpectedStepRevision + 1;
            var updated = Execute(transaction,
                "UPDATE managed_task_step SET revision=$parkedRevision, updated_at=$now " +
                "WHERE run_id=$run AND step_id=$step AND revision=$revision;",
                ("$parkedRevision", parkedStepRevision), ("$now", Format(now)), ("$run", command.RunId),
                ("$step", command.StepId), ("$revision", command.ExpectedStepRevision));
            if (updated != 1)
                return new(ManagedTaskLedgerWriteOutcome.RevisionConflict, null, null);

            Execute(transaction, """
                INSERT INTO managed_task_wait(
                    run_id, step_id, wait_id, reason, reason_detail, detail, evidence,
                    continuation_owner, wake_kind, wake_condition, status, revision, step_revision,
                    deadline, response, created_at, updated_at)
                VALUES($run, $step, $wait, $reason, $reasonDetail, $detail, $evidence,
                    $owner, $wakeKind, $wakeCondition, $status, 0, $stepRevision,
                    $deadline, NULL, $now, $now);
                """, ("$run", command.RunId), ("$step", command.StepId), ("$wait", command.WaitId),
                ("$reason", command.Reason.ToString()), ("$reasonDetail", command.ReasonDetail),
                ("$detail", command.Detail), ("$evidence", command.Evidence),
                ("$owner", command.ContinuationOwner), ("$wakeKind", command.WakeKind.ToString()),
                ("$wakeCondition", command.WakeCondition), ("$status", ManagedTaskWaitStatus.Pending.ToString()),
                ("$stepRevision", parkedStepRevision), ("$deadline", Format(command.Deadline)), ("$now", Format(now)));

            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.WaitParked, now);
            RecordCommand(transaction, command.CommandId, identity, now);
            return new(ManagedTaskLedgerWriteOutcome.Applied,
                RequireWait(command.RunId, command.WaitId, transaction), null);
        }, cancellationToken);
    }

    /// <summary>Commits the response and exactly one continuation intent in one transaction.</summary>
    public Task<ManagedTaskWaitWriteResult> WakeWaitAsync(
        WakeManagedTaskWaitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WaitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExpectedEvidence);
        ValidateBounded(command.Response, ManagedTaskWaitLimits.MaxResponseLength, nameof(command.Response));
        ValidateBounded(command.ContinuationIntentId, ManagedTaskWaitLimits.MaxContinuationIntentIdLength,
            nameof(command.ContinuationIntentId));
        var identity = CommandIdentity.Create("wake-wait", command.RunId, command);
        return WriteWaitAsync(command.CommandId, identity, (transaction, now) =>
        {
            var duplicate = ReadWaitDuplicate(command.CommandId, identity, command.WaitId, transaction);
            if (duplicate is not null)
                return duplicate;

            var snapshot = ReadSnapshot(command.RunId, transaction);
            if (snapshot is null)
                return new(ManagedTaskLedgerWriteOutcome.RunNotFound, null, null);
            var wait = ReadWait(command.RunId, command.WaitId, transaction);
            if (wait is null)
                return new(ManagedTaskLedgerWriteOutcome.WaitNotFound, null, null);
            if (snapshot.Run.Status == ManagedTaskRunStatus.Cancelled || wait.Status == ManagedTaskWaitStatus.Cancelled)
                return new(ManagedTaskLedgerWriteOutcome.Cancelled, wait, null);
            if (IsTerminal(snapshot.Run.Status))
                return new(ManagedTaskLedgerWriteOutcome.Terminal, wait, null);
            if (wait.Status != ManagedTaskWaitStatus.Pending)
                return new(ManagedTaskLedgerWriteOutcome.Terminal, wait, ReadContinuation(command.RunId, command.WaitId, transaction));
            if (wait.Deadline <= now)
            {
                Execute(transaction,
                    "UPDATE managed_task_wait SET status=$status, revision=revision+1, updated_at=$now " +
                    "WHERE run_id=$run AND wait_id=$wait AND status=$pending;",
                    ("$status", ManagedTaskWaitStatus.Expired.ToString()), ("$now", Format(now)),
                    ("$run", command.RunId), ("$wait", command.WaitId),
                    ("$pending", ManagedTaskWaitStatus.Pending.ToString()));
                AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.WaitExpired, now);
                RecordCommand(transaction, command.CommandId, identity, now);
                return new(ManagedTaskLedgerWriteOutcome.Expired,
                    RequireWait(command.RunId, command.WaitId, transaction), null);
            }
            if (wait.Revision != command.ExpectedWaitRevision
                || wait.StepRevision != command.ExpectedStepRevision)
                return new(ManagedTaskLedgerWriteOutcome.RevisionConflict, wait, null);
            var step = snapshot.Steps.SingleOrDefault(item => item.StepId == wait.StepId);
            if (step is null)
                return new(ManagedTaskLedgerWriteOutcome.StepNotFound, wait, null);
            if (step.Revision != wait.StepRevision)
                return new(ManagedTaskLedgerWriteOutcome.RevisionConflict, wait, null);
            if (wait.Evidence != command.ExpectedEvidence || wait.WakeKind != command.WakeKind)
                return new(ManagedTaskLedgerWriteOutcome.EvidenceConflict, wait, null);

            var updated = Execute(transaction,
                "UPDATE managed_task_wait SET status=$status, revision=revision+1, response=$response, updated_at=$now " +
                "WHERE run_id=$run AND wait_id=$wait AND revision=$revision AND step_revision=$stepRevision AND status=$pending;",
                ("$status", ManagedTaskWaitStatus.Woken.ToString()), ("$response", command.Response),
                ("$now", Format(now)), ("$run", command.RunId), ("$wait", command.WaitId),
                ("$revision", command.ExpectedWaitRevision), ("$stepRevision", command.ExpectedStepRevision),
                ("$pending", ManagedTaskWaitStatus.Pending.ToString()));
            if (updated != 1)
                return new(ManagedTaskLedgerWriteOutcome.RevisionConflict, wait, null);
            Execute(transaction, """
                INSERT INTO managed_task_continuation_intent(
                    run_id, step_id, wait_id, intent_id, owner, response, status, generation, created_at, updated_at)
                VALUES($run, $step, $wait, $intent, $owner, $response, $status, 0, $now, $now);
                """, ("$run", command.RunId), ("$step", wait.StepId), ("$wait", command.WaitId),
                ("$intent", command.ContinuationIntentId), ("$owner", wait.ContinuationOwner),
                ("$response", command.Response), ("$status", ManagedTaskContinuationStatus.Pending.ToString()),
                ("$now", Format(now)));
            AppendEvent(transaction, command.RunId, command.CommandId, ManagedTaskEventType.WaitWoken, now);
            RecordCommand(transaction, command.CommandId, identity, now);
            return new(ManagedTaskLedgerWriteOutcome.Applied,
                RequireWait(command.RunId, command.WaitId, transaction),
                ReadContinuation(command.RunId, command.WaitId, transaction));
        }, cancellationToken);
    }

    /// <summary>Claims a pending continuation and advances its consumer generation.</summary>
    public Task<ManagedTaskContinuationWriteResult> ClaimContinuationIntentAsync(
        ClaimManagedTaskContinuationIntentCommand command,
        CancellationToken cancellationToken = default) =>
        ChangeContinuationAsync(command.CommandId, command.RunId, command.IntentId, "claim-continuation",
            command, command.ExpectedGeneration, ManagedTaskContinuationStatus.Pending,
            ManagedTaskContinuationStatus.InProgress, cancellationToken);

    /// <summary>Recovers an interrupted claim for replay and invalidates the old consumer generation.</summary>
    public Task<ManagedTaskContinuationWriteResult> RecoverContinuationIntentAsync(
        RecoverManagedTaskContinuationIntentCommand command,
        CancellationToken cancellationToken = default) =>
        ChangeContinuationAsync(command.CommandId, command.RunId, command.IntentId, "recover-continuation",
            command, command.ExpectedGeneration, ManagedTaskContinuationStatus.InProgress,
            ManagedTaskContinuationStatus.Pending, cancellationToken);

    /// <summary>Completes a continuation only for the generation currently owned by its consumer.</summary>
    public Task<ManagedTaskContinuationWriteResult> CompleteContinuationIntentAsync(
        CompleteManagedTaskContinuationIntentCommand command,
        CancellationToken cancellationToken = default) =>
        ChangeContinuationAsync(command.CommandId, command.RunId, command.IntentId, "complete-continuation",
            command, command.ExpectedGeneration, ManagedTaskContinuationStatus.InProgress,
            ManagedTaskContinuationStatus.Completed, cancellationToken, advanceGeneration: false);

    /// <summary>Reads one durable wait.</summary>
    public async Task<ManagedTaskWaitRecord?> GetWaitAsync(
        string runId,
        string waitId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ReadWait(runId, waitId); }
        finally { _gate.Release(); }
    }

    /// <summary>Reads waits that remain parked across process restarts.</summary>
    public async Task<IReadOnlyList<ManagedTaskWaitRecord>> GetPendingWaitsAsync(
        int maxWaits = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWaits, 1);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ReadWaits(ManagedTaskWaitStatus.Pending, maxWaits); }
        finally { _gate.Release(); }
    }

    /// <summary>Reads continuation intents awaiting owner processing.</summary>
    public Task<IReadOnlyList<ManagedTaskContinuationIntentRecord>> GetPendingContinuationIntentsAsync(
        int maxIntents = 100,
        CancellationToken cancellationToken = default) =>
        GetContinuationIntentsAsync(ManagedTaskContinuationStatus.Pending, maxIntents, cancellationToken);

    /// <summary>Reads claimed intents that a restarted owner must recover before replay.</summary>
    public Task<IReadOnlyList<ManagedTaskContinuationIntentRecord>> GetInProgressContinuationIntentsAsync(
        int maxIntents = 100,
        CancellationToken cancellationToken = default) =>
        GetContinuationIntentsAsync(ManagedTaskContinuationStatus.InProgress, maxIntents, cancellationToken);

    private async Task<IReadOnlyList<ManagedTaskContinuationIntentRecord>> GetContinuationIntentsAsync(
        ManagedTaskContinuationStatus status,
        int maxIntents,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIntents, 1);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT run_id, step_id, wait_id, intent_id, owner, response, status, generation, created_at, updated_at
                FROM managed_task_continuation_intent WHERE status=$status ORDER BY created_at, run_id, intent_id LIMIT $max;
                """;
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$max", maxIntents);
            using var reader = command.ExecuteReader();
            var rows = new List<ManagedTaskContinuationIntentRecord>();
            while (reader.Read())
                rows.Add(ReadContinuation(reader));
            return rows;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Reads accepted completion intents for at-least-once replay. Callers deduplicate with CompletionId;
    /// the ledger does not claim exactly-once external execution or delivery.
    /// </summary>
    public async Task<IReadOnlyList<ManagedTaskUndeliveredResult>> GetUndeliveredAcceptedResultsAsync(
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT d.run_id, d.completion_id, d.step_id, d.attempt_id, d.status, d.generation,
                       d.delivery_attempts, d.max_delivery_attempts, d.last_failure, d.delivery_deadline,
                       d.created_at, d.updated_at,
                       r.attempt_epoch, r.execution_outcome, r.result_reference, r.schema_validation,
                       r.business_acceptance, r.cleanup_status, r.recorded_at
                FROM managed_task_completion_delivery d
                JOIN managed_task_result r ON r.run_id=d.run_id AND r.step_id=d.step_id AND r.attempt_id=d.attempt_id
                WHERE d.status IN ($pending, $inProgress, $failed)
                  AND r.business_acceptance=$accepted
                ORDER BY d.created_at, d.run_id, d.completion_id
                LIMIT $maxResults;
                """;
            command.Parameters.AddWithValue("$pending", ManagedTaskCompletionDeliveryStatus.Pending.ToString());
            command.Parameters.AddWithValue("$inProgress", ManagedTaskCompletionDeliveryStatus.InProgress.ToString());
            command.Parameters.AddWithValue("$failed", ManagedTaskCompletionDeliveryStatus.Failed.ToString());
            command.Parameters.AddWithValue("$accepted", ManagedTaskResultAcceptance.Accepted.ToString());
            command.Parameters.AddWithValue("$maxResults", maxResults);
            using var reader = command.ExecuteReader();
            var rows = new List<ManagedTaskUndeliveredResult>();
            while (reader.Read())
            {
                var delivery = ReadDelivery(reader, 0);
                var result = new ManagedTaskResultRecord(reader.GetString(0), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(12), Enum.Parse<ManagedTaskAttemptStatus>(reader.GetString(13)),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    Enum.Parse<ManagedTaskResultSchemaValidation>(reader.GetString(15)),
                    Enum.Parse<ManagedTaskResultAcceptance>(reader.GetString(16)),
                    Enum.Parse<ManagedTaskResultCleanupStatus>(reader.GetString(17)), Parse(reader.GetString(18)));
                rows.Add(new(result, delivery));
            }
            return rows;
        }
        finally { _gate.Release(); }
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

    private Task<ManagedTaskContinuationWriteResult> ChangeContinuationAsync<T>(
        string commandId,
        string runId,
        string intentId,
        string kind,
        T payload,
        long expectedGeneration,
        ManagedTaskContinuationStatus expectedStatus,
        ManagedTaskContinuationStatus status,
        CancellationToken cancellationToken,
        bool advanceGeneration = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        var identity = CommandIdentity.Create(kind, runId, payload);
        return WriteContinuationAsync(commandId, identity, (transaction, now) =>
        {
            var duplicate = ReadContinuationDuplicate(commandId, identity, intentId, transaction);
            if (duplicate is not null)
                return duplicate;
            var snapshot = ReadSnapshot(runId, transaction);
            if (snapshot is null)
                return new(ManagedTaskLedgerWriteOutcome.RunNotFound, null);
            var intent = ReadContinuationById(runId, intentId, transaction);
            if (intent is null)
                return new(ManagedTaskLedgerWriteOutcome.ContinuationIntentNotFound, null);
            if (snapshot.Run.Status == ManagedTaskRunStatus.Cancelled || intent.Status == ManagedTaskContinuationStatus.Cancelled)
                return new(ManagedTaskLedgerWriteOutcome.Cancelled, intent);
            if (IsTerminal(snapshot.Run.Status))
                return new(ManagedTaskLedgerWriteOutcome.Terminal, intent);
            if (intent.Generation != expectedGeneration)
                return new(ManagedTaskLedgerWriteOutcome.ContinuationGenerationConflict, intent);
            if (intent.Status != expectedStatus)
                return new(ManagedTaskLedgerWriteOutcome.Terminal, intent);

            var generation = advanceGeneration ? expectedGeneration + 1 : expectedGeneration;
            var updated = Execute(transaction, """
                UPDATE managed_task_continuation_intent
                SET status=$status, generation=$nextGeneration, updated_at=$now
                WHERE run_id=$run AND intent_id=$intent AND status=$expectedStatus AND generation=$generation;
                """, ("$status", status.ToString()), ("$nextGeneration", generation), ("$now", Format(now)),
                ("$run", runId), ("$intent", intentId), ("$expectedStatus", expectedStatus.ToString()),
                ("$generation", expectedGeneration));
            if (updated != 1)
                return new(ManagedTaskLedgerWriteOutcome.ContinuationGenerationConflict,
                    ReadContinuationById(runId, intentId, transaction));
            AppendEvent(transaction, runId, commandId, ManagedTaskEventType.ContinuationIntentChanged, now);
            RecordCommand(transaction, commandId, identity, now);
            return new(ManagedTaskLedgerWriteOutcome.Applied,
                ReadContinuationById(runId, intentId, transaction));
        }, cancellationToken);
    }

    private async Task<ManagedTaskContinuationWriteResult> WriteContinuationAsync(
        string commandId,
        CommandIdentity identity,
        Func<SqliteTransaction, DateTimeOffset, ManagedTaskContinuationWriteResult> body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.RunId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            var result = body(transaction, _timeProvider.GetUtcNow());
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

    private async Task<ManagedTaskWaitWriteResult> WriteWaitAsync(
        string commandId,
        CommandIdentity identity,
        Func<SqliteTransaction, DateTimeOffset, ManagedTaskWaitWriteResult> body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.RunId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            var result = body(transaction, _timeProvider.GetUtcNow());
            if (result.Outcome is not (ManagedTaskLedgerWriteOutcome.Applied or ManagedTaskLedgerWriteOutcome.Expired))
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

    private ManagedTaskContinuationWriteResult? ReadContinuationDuplicate(
        string commandId,
        CommandIdentity identity,
        string intentId,
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
            throw new ManagedTaskLedgerConflictException(
                $"Command id '{commandId}' was already used for a different command or run.");
        reader.Close();
        return new(ManagedTaskLedgerWriteOutcome.Duplicate,
            ReadContinuationById(storedRunId, intentId, transaction));
    }

    private ManagedTaskWaitWriteResult? ReadWaitDuplicate(
        string commandId,
        CommandIdentity identity,
        string waitId,
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
            throw new ManagedTaskLedgerConflictException(
                $"Command id '{commandId}' was already used for a different command or run.");

        reader.Close();
        return new(ManagedTaskLedgerWriteOutcome.Duplicate,
            ReadWait(storedRunId, waitId, transaction), ReadContinuation(storedRunId, waitId, transaction));
    }

    private ManagedTaskWaitRecord RequireWait(string runId, string waitId, SqliteTransaction transaction) =>
        ReadWait(runId, waitId, transaction)
        ?? throw new InvalidOperationException($"Managed task wait '{waitId}' disappeared inside a write transaction.");

    private bool HasPendingWait(string runId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM managed_task_wait WHERE run_id=$run AND status=$pending);";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$pending", ManagedTaskWaitStatus.Pending.ToString());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private bool HasPendingWait(string runId, string stepId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM managed_task_wait
                WHERE run_id=$run AND step_id=$step AND status=$pending);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$step", stepId);
        command.Parameters.AddWithValue("$pending", ManagedTaskWaitStatus.Pending.ToString());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private ManagedTaskWaitRecord? ReadWait(
        string runId,
        string waitId,
        SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = WaitSelect + " WHERE run_id=$run AND wait_id=$wait;";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$wait", waitId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWait(reader) : null;
    }

    private IReadOnlyList<ManagedTaskWaitRecord> ReadWaits(ManagedTaskWaitStatus status, int maxWaits)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = WaitSelect + " WHERE status=$status ORDER BY created_at, run_id, wait_id LIMIT $max;";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$max", maxWaits);
        using var reader = command.ExecuteReader();
        var rows = new List<ManagedTaskWaitRecord>();
        while (reader.Read())
            rows.Add(ReadWait(reader));
        return rows;
    }

    private const string WaitSelect = """
        SELECT run_id, step_id, wait_id, reason, reason_detail, detail, evidence,
               continuation_owner, wake_kind, wake_condition, status, revision, step_revision,
               deadline, response, created_at, updated_at
        FROM managed_task_wait
        """;

    private static ManagedTaskWaitRecord ReadWait(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Enum.Parse<ManagedTaskWaitReason>(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), Enum.Parse<ManagedTaskWaitWakeKind>(reader.GetString(8)),
            reader.GetString(9), Enum.Parse<ManagedTaskWaitStatus>(reader.GetString(10)), reader.GetInt64(11),
            reader.GetInt64(12), Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : reader.GetString(14),
            Parse(reader.GetString(15)), Parse(reader.GetString(16)));

    private ManagedTaskContinuationIntentRecord? ReadContinuationById(
        string runId,
        string intentId,
        SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, step_id, wait_id, intent_id, owner, response, status, generation, created_at, updated_at
            FROM managed_task_continuation_intent WHERE run_id=$run AND intent_id=$intent;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$intent", intentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadContinuation(reader) : null;
    }

    private ManagedTaskContinuationIntentRecord? ReadContinuation(
        string runId,
        string waitId,
        SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, step_id, wait_id, intent_id, owner, response, status, generation, created_at, updated_at
            FROM managed_task_continuation_intent WHERE run_id=$run AND wait_id=$wait;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$wait", waitId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadContinuation(reader) : null;
    }

    private static ManagedTaskContinuationIntentRecord ReadContinuation(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5),
            Enum.Parse<ManagedTaskContinuationStatus>(reader.GetString(6)), reader.GetInt64(7),
            Parse(reader.GetString(8)), Parse(reader.GetString(9)));

    private static void ValidateWaitMetadata(ParkManagedTaskWaitCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WaitId);
        ValidateBounded(command.ReasonDetail, ManagedTaskWaitLimits.MaxReasonDetailLength, nameof(command.ReasonDetail));
        ValidateBounded(command.Detail, ManagedTaskWaitLimits.MaxDetailLength, nameof(command.Detail));
        ValidateBounded(command.Evidence, ManagedTaskWaitLimits.MaxEvidenceLength, nameof(command.Evidence));
        ValidateBounded(command.ContinuationOwner, ManagedTaskWaitLimits.MaxContinuationOwnerLength, nameof(command.ContinuationOwner));
        ValidateBounded(command.WakeCondition, ManagedTaskWaitLimits.MaxWakeConditionLength, nameof(command.WakeCondition));
    }

    private static void ValidateBounded(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
            throw new ArgumentException($"{parameterName} cannot exceed {maximumLength} characters.", parameterName);
    }

    private Task<ManagedTaskLedgerWriteResult> ChangeDeliveryAsync<T>(
        string commandId,
        string runId,
        string kind,
        T payload,
        CancellationToken cancellationToken,
        Func<SqliteTransaction, ManagedTaskRunSnapshot, ManagedTaskCompletionDeliveryRecord, DateTimeOffset, ManagedTaskLedgerWriteResult> body)
    {
        var identity = CommandIdentity.Create(kind, runId, payload);
        return WriteAsync(commandId, identity, (transaction, now) =>
        {
            var duplicate = ReadDuplicate(commandId, identity, transaction);
            if (duplicate is not null)
                return duplicate;
            var snapshot = ReadSnapshot(runId, transaction);
            if (snapshot is null)
                return NotFound();
            var completionId = payload switch
            {
                ClaimManagedTaskCompletionDeliveryCommand value => value.CompletionId,
                RecoverManagedTaskCompletionDeliveryCommand value => value.CompletionId,
                CompleteManagedTaskCompletionDeliveryCommand value => value.CompletionId,
                _ => throw new InvalidOperationException("Unsupported delivery command."),
            };
            var delivery = snapshot.CompletionDeliveries.SingleOrDefault(item => item.CompletionId == completionId);
            return delivery is null
                ? Result(ManagedTaskLedgerWriteOutcome.CompletionDeliveryNotFound, snapshot)
                : body(transaction, snapshot, delivery, now);
        }, cancellationToken);
    }

    private ManagedTaskLedgerWriteResult AppliedDelivery<T>(
        string commandId,
        string runId,
        T payload,
        SqliteTransaction transaction,
        DateTimeOffset now,
        ManagedTaskLedgerWriteOutcome outcome = ManagedTaskLedgerWriteOutcome.Applied)
    {
        AppendEvent(transaction, runId, commandId, ManagedTaskEventType.CompletionDeliveryChanged, now);
        RecordCommand(transaction, commandId, CommandIdentity.Create(
            payload is ClaimManagedTaskCompletionDeliveryCommand ? "claim-delivery" :
            payload is RecoverManagedTaskCompletionDeliveryCommand ? "recover-delivery" : "complete-delivery",
            runId, payload), now);
        return Result(outcome, RequireSnapshot(runId, transaction));
    }

    private void UpdateDelivery(
        SqliteTransaction transaction,
        ManagedTaskCompletionDeliveryRecord delivery,
        ManagedTaskCompletionDeliveryStatus status,
        long generation,
        int attempts,
        string? failure,
        DateTimeOffset now) =>
        Execute(transaction, """
            UPDATE managed_task_completion_delivery
            SET status=$status, generation=$generation, delivery_attempts=$attempts,
                last_failure=$failure, updated_at=$now
            WHERE run_id=$run AND completion_id=$completion;
            """, ("$status", status.ToString()), ("$generation", generation), ("$attempts", attempts),
            ("$failure", failure), ("$now", Format(now)), ("$run", delivery.RunId),
            ("$completion", delivery.CompletionId));

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
            var result = body(transaction, _timeProvider.GetUtcNow());
            if (result.Outcome is not (ManagedTaskLedgerWriteOutcome.Applied or ManagedTaskLedgerWriteOutcome.Expired))
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
            CREATE TABLE IF NOT EXISTS managed_task_result(
                run_id TEXT NOT NULL,
                step_id TEXT NOT NULL,
                attempt_id TEXT NOT NULL,
                attempt_epoch INTEGER NOT NULL,
                execution_outcome TEXT NOT NULL,
                result_reference TEXT NULL,
                schema_validation TEXT NOT NULL,
                business_acceptance TEXT NOT NULL,
                cleanup_status TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                PRIMARY KEY(run_id, step_id, attempt_id),
                FOREIGN KEY(run_id, step_id, attempt_id) REFERENCES managed_task_attempt(run_id, step_id, attempt_id));
            CREATE TABLE IF NOT EXISTS managed_task_completion_delivery(
                run_id TEXT NOT NULL,
                completion_id TEXT NOT NULL,
                step_id TEXT NOT NULL,
                attempt_id TEXT NOT NULL,
                status TEXT NOT NULL,
                generation INTEGER NOT NULL,
                delivery_attempts INTEGER NOT NULL,
                max_delivery_attempts INTEGER NOT NULL,
                last_failure TEXT NULL,
                delivery_deadline TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(run_id, completion_id),
                UNIQUE(run_id, step_id, attempt_id),
                FOREIGN KEY(run_id, step_id, attempt_id) REFERENCES managed_task_result(run_id, step_id, attempt_id));
            CREATE TABLE IF NOT EXISTS managed_task_wait(
                run_id TEXT NOT NULL,
                step_id TEXT NOT NULL,
                wait_id TEXT NOT NULL,
                reason TEXT NOT NULL,
                reason_detail TEXT NOT NULL,
                detail TEXT NOT NULL,
                evidence TEXT NOT NULL,
                continuation_owner TEXT NOT NULL,
                wake_kind TEXT NOT NULL,
                wake_condition TEXT NOT NULL,
                status TEXT NOT NULL,
                revision INTEGER NOT NULL,
                step_revision INTEGER NOT NULL,
                deadline TEXT NOT NULL,
                response TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(run_id, wait_id),
                FOREIGN KEY(run_id, step_id) REFERENCES managed_task_step(run_id, step_id));
            CREATE TABLE IF NOT EXISTS managed_task_continuation_intent(
                run_id TEXT NOT NULL,
                step_id TEXT NOT NULL,
                wait_id TEXT NOT NULL,
                intent_id TEXT NOT NULL,
                owner TEXT NOT NULL,
                response TEXT NOT NULL,
                status TEXT NOT NULL,
                generation INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(run_id, intent_id),
                UNIQUE(run_id, wait_id),
                FOREIGN KEY(run_id, wait_id) REFERENCES managed_task_wait(run_id, wait_id));
            CREATE UNIQUE INDEX IF NOT EXISTS ux_managed_task_wait_pending_step
                ON managed_task_wait(run_id, step_id)
                WHERE status = 'Pending';
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
        var results = new List<ManagedTaskResultRecord>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT step_id, attempt_id, attempt_epoch, execution_outcome, result_reference,
                       schema_validation, business_acceptance, cleanup_status, recorded_at
                FROM managed_task_result WHERE run_id=$run ORDER BY step_id, attempt_epoch;
                """;
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new(runId, reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    Enum.Parse<ManagedTaskAttemptStatus>(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    Enum.Parse<ManagedTaskResultSchemaValidation>(reader.GetString(5)),
                    Enum.Parse<ManagedTaskResultAcceptance>(reader.GetString(6)),
                    Enum.Parse<ManagedTaskResultCleanupStatus>(reader.GetString(7)), Parse(reader.GetString(8))));
            }
        }

        var deliveries = new List<ManagedTaskCompletionDeliveryRecord>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT run_id, completion_id, step_id, attempt_id, status, generation, delivery_attempts,
                       max_delivery_attempts, last_failure, delivery_deadline, created_at, updated_at
                FROM managed_task_completion_delivery WHERE run_id=$run ORDER BY created_at, completion_id;
                """;
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                deliveries.Add(ReadDelivery(reader, 0));
        }
        return new(run, new ManagedTaskValueList<ManagedTaskStepRecord>(steps),
            new ManagedTaskValueList<ManagedTaskAttemptRecord>(attempts),
            new ManagedTaskValueList<ManagedTaskResultRecord>(results),
            new ManagedTaskValueList<ManagedTaskCompletionDeliveryRecord>(deliveries));
    }

    private static ManagedTaskCompletionDeliveryRecord ReadDelivery(SqliteDataReader reader, int offset) =>
        new(reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2),
            reader.GetString(offset + 3), Enum.Parse<ManagedTaskCompletionDeliveryStatus>(reader.GetString(offset + 4)),
            reader.GetInt64(offset + 5), reader.GetInt32(offset + 6), reader.GetInt32(offset + 7),
            reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
            reader.IsDBNull(offset + 9) ? null : Parse(reader.GetString(offset + 9)),
            Parse(reader.GetString(offset + 10)), Parse(reader.GetString(offset + 11)));

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
