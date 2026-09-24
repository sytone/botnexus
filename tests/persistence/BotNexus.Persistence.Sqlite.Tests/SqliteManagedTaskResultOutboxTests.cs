namespace BotNexus.Persistence.Sqlite.Tests;

public sealed class SqliteManagedTaskResultOutboxTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("botnexus-result-outbox-").FullName;
    private string DbPath => Path.Combine(_directory, "ledger.db");

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_directory);
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Acceptance_and_completion_intent_commit_atomically_and_replay_after_restart()
    {
        await using (var ledger = await CreateSucceededAttemptAsync())
        {
            var accepted = await ledger.RecordResultAsync(Accept("accept-1"));
            accepted.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
            Snapshot(accepted).Results.Single().BusinessAcceptance.ShouldBe(ManagedTaskResultAcceptance.Accepted);
            Snapshot(accepted).Results.Single().CleanupStatus.ShouldBe(ManagedTaskResultCleanupStatus.Pending);
            Snapshot(accepted).CompletionDeliveries.Single().Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Pending);
        }

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath);
        var replay = await reopened.GetUndeliveredAcceptedResultsAsync();
        replay.Count.ShouldBe(1);
        replay.Single().Result.AttemptId.ShouldBe("attempt-1");
        replay.Single().Delivery.Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Pending);
        Snapshot(await reopened.RecordResultAsync(Accept("accept-1"))).Attempts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Rejected_result_is_separate_from_execution_and_has_no_delivery()
    {
        await using var ledger = await CreateSucceededAttemptAsync();
        var rejected = await ledger.RecordResultAsync(new(
            "reject-1", "run-1", "step-1", "attempt-1", 2, 1,
            ManagedTaskResultSchemaValidation.Invalid, ManagedTaskResultAcceptance.Rejected,
            CompletionId: null, MaxDeliveryAttempts: 0, DeliveryDeadline: null));

        var result = Snapshot(rejected).Results.Single();
        result.ExecutionOutcome.ShouldBe(ManagedTaskAttemptStatus.Succeeded);
        result.SchemaValidation.ShouldBe(ManagedTaskResultSchemaValidation.Invalid);
        result.BusinessAcceptance.ShouldBe(ManagedTaskResultAcceptance.Rejected);
        Snapshot(rejected).CompletionDeliveries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Accepted_failed_attempt_cannot_be_readmitted_for_reexecution()
    {
        await using var ledger = await CreateTerminalAttemptAsync(
            ManagedTaskAttemptStatus.Failed, "result://failed-but-accepted");
        var accepted = await ledger.RecordResultAsync(Accept("accept-failed"));

        Snapshot(accepted).Results.Single().ExecutionOutcome.ShouldBe(ManagedTaskAttemptStatus.Failed);
        Snapshot(accepted).Results.Single().BusinessAcceptance.ShouldBe(ManagedTaskResultAcceptance.Accepted);

        var readmit = await ledger.AdmitAttemptAsync(new(
            "readmit-after-accept", "run-1", "step-1",
            Snapshot(accepted).Steps.Single().Revision, "attempt-2"));

        readmit.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Terminal);
        Snapshot(readmit).Attempts.ShouldHaveSingleItem().AttemptId.ShouldBe("attempt-1");
    }

    [Fact]
    public async Task Cleanup_transition_does_not_change_acceptance_or_delivery()
    {
        await using var ledger = await CreateSucceededAttemptAsync();
        await ledger.RecordResultAsync(Accept("accept-1"));

        var cleaned = await ledger.CompleteResultCleanupAsync(
            new("cleanup-1", "run-1", "step-1", "attempt-1", 1));

        Snapshot(cleaned).Results.Single().CleanupStatus.ShouldBe(ManagedTaskResultCleanupStatus.Completed);
        Snapshot(cleaned).Results.Single().BusinessAcceptance.ShouldBe(ManagedTaskResultAcceptance.Accepted);
        Snapshot(cleaned).CompletionDeliveries.Single().Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Pending);
    }

    [Fact]
    public async Task Crash_before_accept_commit_leaves_neither_result_nor_delivery()
    {
        await using (var setup = await CreateSucceededAttemptAsync()) { }
        await using (var crashing = new SqliteManagedTaskFlowLedger(
            DbPath, new ThrowingObserver("accept-crash")))
        {
            await Should.ThrowAsync<SimulatedCrashException>(
                async () => await crashing.RecordResultAsync(Accept("accept-crash")));
        }

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath);
        var snapshot = await reopened.GetRunAsync("run-1");
        Assert.NotNull(snapshot);
        snapshot.Results.ShouldBeEmpty();
        snapshot.CompletionDeliveries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recovery_fences_stale_delivery_and_bounded_failures_preserve_exact_error()
    {
        await using var ledger = await CreateSucceededAttemptAsync();
        await ledger.RecordResultAsync(Accept("accept-1", maxAttempts: 2));
        await ledger.ClaimCompletionDeliveryAsync(new("claim-1", "run-1", "completion-1", 0));
        var interrupted = (await ledger.GetUndeliveredAcceptedResultsAsync()).Single();
        interrupted.Delivery.Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.InProgress);
        var recovered = await ledger.RecoverCompletionDeliveryAsync(new("recover-1", "run-1", "completion-1", 1));
        Delivery(recovered).Generation.ShouldBe(2);
        Delivery(recovered).Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Pending);
        Delivery(recovered).LastFailure.ShouldBe("delivery interrupted while attempt was in progress");

        var stale = await ledger.CompleteCompletionDeliveryAsync(new(
            "stale", "run-1", "completion-1", 1,
            ManagedTaskCompletionDeliveryStatus.Delivered, LastFailure: null));
        stale.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.DeliveryGenerationConflict);

        await ledger.ClaimCompletionDeliveryAsync(new("claim-2", "run-1", "completion-1", 2));
        var failed = await ledger.CompleteCompletionDeliveryAsync(new(
            "fail-1", "run-1", "completion-1", 3,
            ManagedTaskCompletionDeliveryStatus.Failed, "HTTP 503: upstream unavailable"));
        Delivery(failed).Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Suspended);
        Delivery(failed).LastFailure.ShouldBe("HTTP 503: upstream unavailable");
        (await ledger.GetUndeliveredAcceptedResultsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Transient_failure_replays_and_success_becomes_delivered()
    {
        await using var ledger = await CreateSucceededAttemptAsync();
        await ledger.RecordResultAsync(Accept("accept-1", maxAttempts: 3));
        await ledger.ClaimCompletionDeliveryAsync(new("claim-1", "run-1", "completion-1", 0));
        var failed = await ledger.CompleteCompletionDeliveryAsync(new(
            "fail-1", "run-1", "completion-1", 1,
            ManagedTaskCompletionDeliveryStatus.Failed, "connection reset by peer"));
        Delivery(failed).Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Failed);
        (await ledger.GetUndeliveredAcceptedResultsAsync()).Single().Delivery.LastFailure
            .ShouldBe("connection reset by peer");

        await ledger.ClaimCompletionDeliveryAsync(new("claim-2", "run-1", "completion-1", 1));
        var delivered = await ledger.CompleteCompletionDeliveryAsync(new(
            "delivered-1", "run-1", "completion-1", 2,
            ManagedTaskCompletionDeliveryStatus.Delivered, LastFailure: null));
        Delivery(delivered).Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Delivered);
        (await ledger.GetUndeliveredAcceptedResultsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deadline_and_late_result_boundaries_are_deterministic()
    {
        var clock = new MutableTimeProvider(new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
        await using var ledger = await CreateSucceededAttemptAsync(clock);
        await ledger.RecordResultAsync(Accept("accept-1", deadline: clock.GetUtcNow().AddMinutes(1)));
        clock.Advance(TimeSpan.FromMinutes(2));

        var expired = await ledger.ClaimCompletionDeliveryAsync(new("expired", "run-1", "completion-1", 0));
        expired.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Expired);
        Delivery(expired).Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Discarded);

        var late = await ledger.RecordResultAsync(Accept("late") with { ExpectedStepRevision = 1 });
        late.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.RevisionConflict);
        var wrongEpoch = await ledger.RecordResultAsync(Accept("stale-epoch") with { ExpectedAttemptEpoch = 2 });
        wrongEpoch.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.EpochConflict);
    }

    [Fact]
    public async Task Delivered_completion_after_deadline_remains_terminal_evidence()
    {
        var clock = new MutableTimeProvider(new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
        await using var ledger = await CreateSucceededAttemptAsync(clock);
        await ledger.RecordResultAsync(Accept(
            "accept-delivered", deadline: clock.GetUtcNow().AddMinutes(1)));
        await ledger.ClaimCompletionDeliveryAsync(new(
            "claim-delivered", "run-1", "completion-1", 0));
        var delivered = await ledger.CompleteCompletionDeliveryAsync(new(
            "complete-delivered", "run-1", "completion-1", 1,
            ManagedTaskCompletionDeliveryStatus.Delivered, LastFailure: null));
        clock.Advance(TimeSpan.FromMinutes(2));

        var lateClaim = await ledger.ClaimCompletionDeliveryAsync(new(
            "claim-after-deadline", "run-1", "completion-1", 1));

        lateClaim.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Terminal);
        Delivery(lateClaim).ShouldBe(Delivery(delivered));
        Delivery(lateClaim).Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Delivered);
        Delivery(lateClaim).Generation.ShouldBe(1);
        Delivery(lateClaim).LastFailure.ShouldBeNull();
    }

    [Fact]
    public async Task Result_decision_is_refused_after_actual_cancellation_and_cancellation_stays_sticky()
    {
        await using var ledger = await CreateSucceededAttemptAsync();
        var cancelled = await ledger.CancelRunAsync(new("cancel", "run-1", 0, "operator stop"));
        var refused = await ledger.RecordResultAsync(Accept("accept-cancelled"));

        refused.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Cancelled);
        Snapshot(refused).Run.Status.ShouldBe(ManagedTaskRunStatus.Cancelled);
        Snapshot(refused).Run.CancellationReason.ShouldBe("operator stop");
        Snapshot(refused).Results.ShouldBeEmpty();
        Snapshot(cancelled).Run.Status.ShouldBe(ManagedTaskRunStatus.Cancelled);
    }

    [Fact]
    public async Task Result_decision_is_refused_after_actual_completed_run()
    {
        await using var ledger = await CreateSucceededAttemptAsync();
        var running = await ledger.TransitionRunAsync(new("run", "run-1", 0, ManagedTaskRunStatus.Running));
        await ledger.TransitionRunAsync(new(
            "complete", "run-1", Snapshot(running).Run.Revision, ManagedTaskRunStatus.Completed));

        var terminal = await ledger.RecordResultAsync(Accept("accept-completed"));
        terminal.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Terminal);
        Snapshot(terminal).Run.Status.ShouldBe(ManagedTaskRunStatus.Completed);
        Snapshot(terminal).Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reopen_surfaces_expired_delivery_for_durable_bounded_discard()
    {
        var clock = new MutableTimeProvider(new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
        await using (var ledger = await CreateSucceededAttemptAsync(clock))
            await ledger.RecordResultAsync(Accept("accept-expiring", deadline: clock.GetUtcNow().AddMinutes(1)));

        clock.Advance(TimeSpan.FromMinutes(2));
        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: clock);
        var replay = await reopened.GetUndeliveredAcceptedResultsAsync(maxResults: 1);
        replay.ShouldHaveSingleItem().Delivery.Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Pending);
        var discarded = await reopened.ClaimCompletionDeliveryAsync(new(
            "discard-expired", "run-1", replay.Single().Delivery.CompletionId, replay.Single().Delivery.Generation));
        discarded.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Expired);
        Delivery(discarded).Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Discarded);
        (await reopened.GetUndeliveredAcceptedResultsAsync(maxResults: 1)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Recovery_of_interrupted_final_attempt_suspends_with_exact_failure()
    {
        await using var ledger = await CreateSucceededAttemptAsync();
        await ledger.RecordResultAsync(Accept("accept-1", maxAttempts: 1));
        await ledger.ClaimCompletionDeliveryAsync(new("claim-final", "run-1", "completion-1", 0));
        var recovered = await ledger.RecoverCompletionDeliveryAsync(new(
            "recover-final", "run-1", "completion-1", 1));
        recovered.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Delivery(recovered).Status.ShouldBe(ManagedTaskCompletionDeliveryStatus.Suspended);
        Delivery(recovered).LastFailure.ShouldBe("delivery interrupted while final allowed attempt was in progress");
        (await ledger.GetUndeliveredAcceptedResultsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_side_effect_is_parked_for_explicit_reconciliation()
    {
        await using var ledger = await CreateTerminalAttemptAsync(
            ManagedTaskAttemptStatus.UnknownSideEffect, "side-effect://unknown");
        var accepted = await ledger.RecordResultAsync(Accept("accept-unknown"));
        var rejected = await ledger.RecordResultAsync(Accept("reject-unknown") with
        {
            BusinessAcceptance = ManagedTaskResultAcceptance.Rejected,
            CompletionId = null,
            MaxDeliveryAttempts = 0,
        });
        accepted.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.ReconciliationRequired);
        rejected.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.ReconciliationRequired);
        Snapshot(rejected).Results.ShouldBeEmpty();
        Snapshot(rejected).Attempts.Single().Status.ShouldBe(ManagedTaskAttemptStatus.UnknownSideEffect);
    }

    [Fact]
    public async Task Completion_id_collision_across_attempts_returns_stable_ledger_conflict()
    {
        await using var ledger = new SqliteManagedTaskFlowLedger(DbPath);
        await ledger.CreateRunAsync(new("create-1", Specification(includeSecondStep: true)));
        await CompleteAttemptAsync(ledger, "step-1", "attempt-1", 0, 1, "result://artifact/1");
        await CompleteAttemptAsync(ledger, "step-2", "attempt-2", 0, 1, "result://artifact/2");
        await ledger.RecordResultAsync(Accept("accept-first"));
        var collision = await ledger.RecordResultAsync(Accept("accept-second") with
        {
            StepId = "step-2",
            AttemptId = "attempt-2",
        });
        collision.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.LedgerConflict);
        Snapshot(collision).Results.Count.ShouldBe(1);
        Snapshot(collision).CompletionDeliveries.Single().AttemptId.ShouldBe("attempt-1");
    }

    private RecordManagedTaskResultCommand Accept(string commandId, int maxAttempts = 3, DateTimeOffset? deadline = null) =>
        new(commandId, "run-1", "step-1", "attempt-1", 2, 1,
            ManagedTaskResultSchemaValidation.Valid, ManagedTaskResultAcceptance.Accepted,
            "completion-1", maxAttempts, deadline);

    private async Task<SqliteManagedTaskFlowLedger> CreateSucceededAttemptAsync(TimeProvider? clock = null)
        => await CreateTerminalAttemptAsync(ManagedTaskAttemptStatus.Succeeded, "result://artifact/1", clock);

    private async Task<SqliteManagedTaskFlowLedger> CreateTerminalAttemptAsync(
        ManagedTaskAttemptStatus status, string? resultReference, TimeProvider? clock = null)
    {
        var ledger = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: clock);
        await ledger.CreateRunAsync(new("create-1", Specification()));
        await CompleteAttemptAsync(ledger, "step-1", "attempt-1", 0, 1, resultReference, status);
        return ledger;
    }

    private static async Task CompleteAttemptAsync(
        SqliteManagedTaskFlowLedger ledger, string stepId, string attemptId, long expectedStepRevision,
        long expectedAttemptEpoch, string? resultReference,
        ManagedTaskAttemptStatus status = ManagedTaskAttemptStatus.Succeeded)
    {
        await ledger.AdmitAttemptAsync(new(
            $"admit-{attemptId}", "run-1", stepId, expectedStepRevision, attemptId));
        await ledger.CommitAttemptAsync(new(
            $"finish-{attemptId}", "run-1", stepId, attemptId, expectedStepRevision + 1,
            expectedAttemptEpoch, status, resultReference));
    }

    private static ManagedTaskRunSpecification Specification(bool includeSecondStep = false) => new(
        "run-1", "{}", "input://release",
        new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
        new ManagedTaskResourceBounds(12, TimeSpan.FromMinutes(5), 1), null,
        includeSecondStep
            ? [
                new ManagedTaskStepSpecification("step-1", "task://build",
                    new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
                    new ManagedTaskResourceBounds(8, TimeSpan.FromMinutes(2), 1)),
                new ManagedTaskStepSpecification("step-2", "task://publish",
                    new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
                    new ManagedTaskResourceBounds(8, TimeSpan.FromMinutes(2), 1)),
            ]
            : [new ManagedTaskStepSpecification("step-1", "task://build",
                new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
                new ManagedTaskResourceBounds(8, TimeSpan.FromMinutes(2), 1))]);

    private static ManagedTaskRunSnapshot Snapshot(ManagedTaskLedgerWriteResult result)
    {
        Assert.NotNull(result.Snapshot);
        return result.Snapshot;
    }

    private static ManagedTaskCompletionDeliveryRecord Delivery(ManagedTaskLedgerWriteResult result) =>
        Snapshot(result).CompletionDeliveries.Single();

    private sealed class ThrowingObserver(string command) : IManagedTaskFlowCommitObserver
    {
        public void OnCommitPoint(ManagedTaskFlowCommitPoint observed, string commandId)
        {
            if (observed == ManagedTaskFlowCommitPoint.BeforeCommit && commandId == command)
                throw new SimulatedCrashException(commandId);
        }
    }

    private sealed class SimulatedCrashException(string commandId) : Exception(commandId);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
