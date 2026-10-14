namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Persistence contract for the smallest durable managed-task wait slice (#4291). Scheduling and
/// event transport remain outside the ledger; callers adapt existing timer and world-event seams
/// into fenced wake commands.
/// </summary>
public sealed class SqliteManagedTaskWaitLedgerTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-managed-task-waits-").FullName;

    private string DbPath => Path.Combine(_directory, "ledger.db");

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_directory);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a held SQLite WAL handle on Windows must not fail the suite.
        }
    }

    [Fact]
    public async Task Pending_wait_carries_bounded_resume_evidence_and_survives_restart()
    {
        var clock = new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var deadline = clock.GetUtcNow().AddHours(2);
        var command = Park(
            reason: ManagedTaskWaitReason.AskUser,
            detail: "Which deployment ring should receive release 2026.09?",
            evidence: "prompt-sha256:6d27",
            owner: "agent:release-manager",
            wakeKind: ManagedTaskWaitWakeKind.Answer,
            wakeCondition: "conversation-response:question-17",
            deadline: deadline);

        await using (var ledger = await CreateLedgerAsync(clock))
        {
            var parked = await ledger.ParkWaitAsync(command);
            parked.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
            Wait(parked).Status.ShouldBe(ManagedTaskWaitStatus.Pending);
            Wait(parked).Revision.ShouldBe(0);
            Wait(parked).StepRevision.ShouldBe(1);
        }

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: clock);
        var wait = (await reopened.GetPendingWaitsAsync()).ShouldHaveSingleItem();
        wait.WaitId.ShouldBe("wait-1");
        wait.RunId.ShouldBe("run-1");
        wait.StepId.ShouldBe("step-1");
        wait.Reason.ShouldBe(ManagedTaskWaitReason.AskUser);
        wait.Detail.ShouldBe(command.Detail);
        wait.Evidence.ShouldBe(command.Evidence);
        wait.ContinuationOwner.ShouldBe(command.ContinuationOwner);
        wait.WakeKind.ShouldBe(ManagedTaskWaitWakeKind.Answer);
        wait.WakeCondition.ShouldBe(command.WakeCondition);
        wait.Revision.ShouldBe(0);
        wait.StepRevision.ShouldBe(1);
        wait.Deadline.ShouldBe(deadline);
        wait.Response.ShouldBeNull();

        await Should.ThrowAsync<ArgumentException>(async () =>
            await reopened.ParkWaitAsync(command with
            {
                CommandId = "oversized-reason",
                WaitId = "oversized-reason",
                ReasonDetail = new string('r', ManagedTaskWaitLimits.MaxReasonDetailLength + 1),
            }));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await reopened.ParkWaitAsync(command with
            {
                CommandId = "oversized-detail",
                WaitId = "oversized-detail",
                Detail = new string('d', ManagedTaskWaitLimits.MaxDetailLength + 1),
            }));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await reopened.ParkWaitAsync(command with
            {
                CommandId = "oversized-evidence",
                WaitId = "oversized-evidence",
                Evidence = new string('e', ManagedTaskWaitLimits.MaxEvidenceLength + 1),
            }));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await reopened.ParkWaitAsync(command with
            {
                CommandId = "oversized-owner",
                WaitId = "oversized-owner",
                ContinuationOwner = new string('o', ManagedTaskWaitLimits.MaxContinuationOwnerLength + 1),
            }));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await reopened.ParkWaitAsync(command with
            {
                CommandId = "oversized-condition",
                WaitId = "oversized-condition",
                WakeCondition = new string('w', ManagedTaskWaitLimits.MaxWakeConditionLength + 1),
            }));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await reopened.WakeWaitAsync(Wake("oversized-response", ManagedTaskWaitWakeKind.Answer,
                new string('r', ManagedTaskWaitLimits.MaxResponseLength + 1))));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await reopened.WakeWaitAsync(Wake("oversized-intent", ManagedTaskWaitWakeKind.Answer, "ring-2") with
            {
                ContinuationIntentId = new string('i', ManagedTaskWaitLimits.MaxContinuationIntentIdLength + 1),
            }));
    }

    [Theory]
    [InlineData(ManagedTaskWaitReason.AskUser, ManagedTaskWaitWakeKind.Answer, "ring-2")]
    [InlineData(ManagedTaskWaitReason.Approval, ManagedTaskWaitWakeKind.Approval, "approved")]
    public async Task Answer_or_approval_commits_response_and_one_pending_continuation_atomically(
        ManagedTaskWaitReason reason,
        ManagedTaskWaitWakeKind wakeKind,
        string response)
    {
        await using (var ledger = await CreateLedgerAsync())
        {
            await ledger.ParkWaitAsync(Park(reason: reason, wakeKind: wakeKind));
            var woken = await ledger.WakeWaitAsync(Wake("wake-1", wakeKind, response));

            woken.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
            Wait(woken).Status.ShouldBe(ManagedTaskWaitStatus.Woken);
            Wait(woken).Revision.ShouldBe(1);
            Wait(woken).Response.ShouldBe(response);
            Continuation(woken).IntentId.ShouldBe("continuation-1");
            Continuation(woken).Owner.ShouldBe("agent:release-manager");
            Continuation(woken).Status.ShouldBe(ManagedTaskContinuationStatus.Pending);
            Continuation(woken).Generation.ShouldBe(0);
        }

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)));
        var durableWait = await reopened.GetWaitAsync("run-1", "wait-1");
        Assert.NotNull(durableWait);
        durableWait.Response.ShouldBe(response);
        durableWait.Status.ShouldBe(ManagedTaskWaitStatus.Woken);
        var pending = (await reopened.GetPendingContinuationIntentsAsync()).ShouldHaveSingleItem();
        pending.IntentId.ShouldBe("continuation-1");
        pending.WaitId.ShouldBe("wait-1");
        pending.Status.ShouldBe(ManagedTaskContinuationStatus.Pending);
    }

    [Fact]
    public async Task Wake_is_idempotent_and_rejects_stale_revision_or_evidence_without_resuming_twice()
    {
        await using var ledger = await CreateLedgerAsync();
        await ledger.ParkWaitAsync(Park());

        var staleRevision = await ledger.WakeWaitAsync(
            Wake("stale-revision", ManagedTaskWaitWakeKind.Answer, "ring-2") with
            {
                ExpectedWaitRevision = 1,
            });
        staleRevision.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.RevisionConflict);

        var staleEvidence = await ledger.WakeWaitAsync(
            Wake("stale-evidence", ManagedTaskWaitWakeKind.Answer, "ring-2") with
            {
                ExpectedEvidence = "prompt-sha256:obsolete",
            });
        staleEvidence.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.EvidenceConflict);

        var applied = await ledger.WakeWaitAsync(
            Wake("wake-once", ManagedTaskWaitWakeKind.Answer, "ring-2"));
        applied.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);

        var duplicate = await ledger.WakeWaitAsync(
            Wake("wake-once", ManagedTaskWaitWakeKind.Answer, "ring-2"));
        duplicate.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Duplicate);
        Wait(duplicate).ShouldBe(Wait(applied));
        Continuation(duplicate).ShouldBe(Continuation(applied));
        (await ledger.GetPendingContinuationIntentsAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task One_pending_wait_owns_the_step_and_attempt_admission_cannot_bypass_it()
    {
        await using var ledger = await CreateLedgerAsync();
        var parked = await ledger.ParkWaitAsync(Park());
        Wait(parked).StepRevision.ShouldBe(1);

        var secondWait = await ledger.ParkWaitAsync(Park() with
        {
            CommandId = "park-2",
            WaitId = "wait-2",
            ExpectedStepRevision = 1,
        });
        secondWait.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.PendingWaitConflict);

        var evolved = await ledger.AdmitAttemptAsync(new("admit-after-park", "run-1", "step-1", 1, "attempt-1"));
        evolved.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.PendingWaitConflict);

        var staleWake = await ledger.WakeWaitAsync(
            Wake("wake-after-step-evolved", ManagedTaskWaitWakeKind.Answer, "ring-2"));
        staleWake.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Wait(staleWake).Status.ShouldBe(ManagedTaskWaitStatus.Woken);
        (await ledger.GetPendingContinuationIntentsAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Duplicate_consumers_cannot_claim_or_complete_one_continuation_twice()
    {
        await using var first = await CreateLedgerAsync();
        await first.ParkWaitAsync(Park());
        await first.WakeWaitAsync(Wake("wake-before-claim", ManagedTaskWaitWakeKind.Answer, "ring-2"));
        await using var second = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)));

        var claimed = await first.ClaimContinuationIntentAsync(
            new("claim-first", "run-1", "continuation-1", 0));
        claimed.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Intent(claimed).Status.ShouldBe(ManagedTaskContinuationStatus.InProgress);
        Intent(claimed).Generation.ShouldBe(1);

        var duplicateClaim = await second.ClaimContinuationIntentAsync(
            new("claim-second", "run-1", "continuation-1", 0));
        duplicateClaim.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.ContinuationGenerationConflict);
        Intent(duplicateClaim).Generation.ShouldBe(1);

        var completed = await first.CompleteContinuationIntentAsync(
            new("complete-first", "run-1", "continuation-1", 1));
        completed.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Intent(completed).Status.ShouldBe(ManagedTaskContinuationStatus.Completed);

        var duplicateComplete = await second.CompleteContinuationIntentAsync(
            new("complete-second", "run-1", "continuation-1", 1));
        duplicateComplete.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Terminal);
        Intent(duplicateComplete).Status.ShouldBe(ManagedTaskContinuationStatus.Completed);
        (await first.GetPendingContinuationIntentsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Restart_recovers_interrupted_continuation_claim_and_fences_old_consumer()
    {
        await using (var ledger = await CreateLedgerAsync())
        {
            await ledger.ParkWaitAsync(Park());
            await ledger.WakeWaitAsync(Wake("wake-for-recovery", ManagedTaskWaitWakeKind.Answer, "ring-2"));
            var claimed = await ledger.ClaimContinuationIntentAsync(
                new("claim-before-restart", "run-1", "continuation-1", 0));
            Intent(claimed).Generation.ShouldBe(1);
        }

        await using var recoveredLedger = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)));
        var interrupted = (await recoveredLedger.GetInProgressContinuationIntentsAsync()).ShouldHaveSingleItem();
        interrupted.IntentId.ShouldBe("continuation-1");
        interrupted.Generation.ShouldBe(1);
        var recovered = await recoveredLedger.RecoverContinuationIntentAsync(
            new("recover-after-restart", "run-1", "continuation-1", 1));
        recovered.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Intent(recovered).Status.ShouldBe(ManagedTaskContinuationStatus.Pending);
        Intent(recovered).Generation.ShouldBe(2);

        var staleCompletion = await recoveredLedger.CompleteContinuationIntentAsync(
            new("stale-complete", "run-1", "continuation-1", 1));
        staleCompletion.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.ContinuationGenerationConflict);

        var reclaimed = await recoveredLedger.ClaimContinuationIntentAsync(
            new("reclaim", "run-1", "continuation-1", 2));
        Intent(reclaimed).Generation.ShouldBe(3);
        var completed = await recoveredLedger.CompleteContinuationIntentAsync(
            new("complete-reclaimed", "run-1", "continuation-1", 3));
        completed.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Intent(completed).Status.ShouldBe(ManagedTaskContinuationStatus.Completed);
    }

    [Fact]
    public async Task Crash_before_wake_commit_leaves_response_and_continuation_absent()
    {
        await using (var setup = await CreateLedgerAsync())
            await setup.ParkWaitAsync(Park());

        await using (var crashing = new SqliteManagedTaskFlowLedger(
            DbPath,
            new ThrowingCommitObserver("wake-crash", ManagedTaskFlowCommitPoint.BeforeCommit),
            new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero))))
        {
            await Should.ThrowAsync<SimulatedManagedTaskWaitCrashException>(async () =>
                await crashing.WakeWaitAsync(
                    Wake("wake-crash", ManagedTaskWaitWakeKind.Answer, "ring-2")));
        }

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)));
        var wait = await reopened.GetWaitAsync("run-1", "wait-1");
        Assert.NotNull(wait);
        wait.Status.ShouldBe(ManagedTaskWaitStatus.Pending);
        wait.Revision.ShouldBe(0);
        wait.Response.ShouldBeNull();
        (await reopened.GetPendingContinuationIntentsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ManagedTaskWaitReason.Timer, ManagedTaskWaitWakeKind.Timer, "timer:2026-09-28T12:05:00Z")]
    [InlineData(ManagedTaskWaitReason.Event, ManagedTaskWaitWakeKind.Event, "world-event:deployment/ready")]
    public async Task Timer_and_event_waits_share_one_adapter_facing_ledger_contract(
        ManagedTaskWaitReason reason,
        ManagedTaskWaitWakeKind wakeKind,
        string condition)
    {
        await using var ledger = await CreateLedgerAsync();
        await ledger.ParkWaitAsync(Park(
            reason: reason,
            wakeKind: wakeKind,
            wakeCondition: condition));

        var woken = await ledger.WakeWaitAsync(Wake("adapter-wake", wakeKind, condition));

        woken.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Wait(woken).WakeKind.ShouldBe(wakeKind);
        Wait(woken).Response.ShouldBe(condition);
        Continuation(woken).Status.ShouldBe(ManagedTaskContinuationStatus.Pending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_and_wake_arbitrate_by_durable_commit_order(bool cancellationFirst)
    {
        await using var cancellationWriter = await CreateLedgerAsync();
        await cancellationWriter.ParkWaitAsync(Park());
        await using var wakeWriter = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)));

        if (cancellationFirst)
        {
            var cancellation = await cancellationWriter.CancelRunAsync(
                new("cancel-first", "run-1", 0, "operator stop"));
            var wake = await wakeWriter.WakeWaitAsync(
                Wake("wake-second", ManagedTaskWaitWakeKind.Answer, "ring-2"));

            cancellation.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
            wake.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Cancelled);
            Wait(wake).Status.ShouldBe(ManagedTaskWaitStatus.Cancelled);
            Wait(wake).Response.ShouldBeNull();
            wake.ContinuationIntent.ShouldBeNull();
            (await cancellationWriter.GetPendingContinuationIntentsAsync()).ShouldBeEmpty();
        }
        else
        {
            var wake = await wakeWriter.WakeWaitAsync(
                Wake("wake-first", ManagedTaskWaitWakeKind.Answer, "ring-2"));
            var cancellation = await cancellationWriter.CancelRunAsync(
                new("cancel-second", "run-1", 0, "operator stop"));

            wake.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
            cancellation.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
            Wait(wake).Status.ShouldBe(ManagedTaskWaitStatus.Woken);
            Continuation(wake).Status.ShouldBe(ManagedTaskContinuationStatus.Pending);
            (await cancellationWriter.GetPendingContinuationIntentsAsync()).ShouldBeEmpty();
            var claim = await wakeWriter.ClaimContinuationIntentAsync(
                new("claim-after-cancel", "run-1", "continuation-1", 1));
            claim.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Cancelled);
            Intent(claim).Status.ShouldBe(ManagedTaskContinuationStatus.Cancelled);
        }
    }

    [Fact]
    public async Task Deadline_expiry_wins_at_the_boundary_and_never_leaves_a_continuation()
    {
        var clock = new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        await using var ledger = await CreateLedgerAsync(clock);
        await ledger.ParkWaitAsync(Park(deadline: clock.GetUtcNow().AddMinutes(5)));
        clock.Advance(TimeSpan.FromMinutes(5));

        var wakeAtDeadline = await ledger.WakeWaitAsync(
            Wake("wake-at-deadline", ManagedTaskWaitWakeKind.Answer, "ring-2"));

        wakeAtDeadline.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Expired);
        Wait(wakeAtDeadline).Status.ShouldBe(ManagedTaskWaitStatus.Expired);
        Wait(wakeAtDeadline).Response.ShouldBeNull();
        wakeAtDeadline.ContinuationIntent.ShouldBeNull();
        (await ledger.GetPendingContinuationIntentsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Restart_recovers_both_pending_waits_and_pending_continuation_intents()
    {
        await using (var ledger = await CreateLedgerAsync())
            await ledger.ParkWaitAsync(Park());

        await using (var reopened = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero))))
        {
            (await reopened.GetPendingWaitsAsync()).ShouldHaveSingleItem().WaitId.ShouldBe("wait-1");
            await reopened.WakeWaitAsync(
                Wake("wake-before-second-restart", ManagedTaskWaitWakeKind.Answer, "ring-2"));
        }

        await using var recovered = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)));
        (await recovered.GetPendingWaitsAsync()).ShouldBeEmpty();
        var intent = (await recovered.GetPendingContinuationIntentsAsync()).ShouldHaveSingleItem();
        intent.IntentId.ShouldBe("continuation-1");
        intent.Status.ShouldBe(ManagedTaskContinuationStatus.Pending);
        intent.Response.ShouldBe("ring-2");
    }

    private async Task<SqliteManagedTaskFlowLedger> CreateLedgerAsync(TimeProvider? clock = null)
    {
        clock ??= new MutableTimeProvider(new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var ledger = new SqliteManagedTaskFlowLedger(DbPath, timeProvider: clock);
        await ledger.CreateRunAsync(new("create-1", Specification()));
        return ledger;
    }

    private static ParkManagedTaskWaitCommand Park(
        ManagedTaskWaitReason reason = ManagedTaskWaitReason.AskUser,
        string detail = "Which deployment ring should receive release 2026.09?",
        string evidence = "prompt-sha256:6d27",
        string owner = "agent:release-manager",
        ManagedTaskWaitWakeKind wakeKind = ManagedTaskWaitWakeKind.Answer,
        string wakeCondition = "conversation-response:question-17",
        DateTimeOffset? deadline = null) =>
        new(
            "park-1",
            "run-1",
            "step-1",
            "wait-1",
            reason,
            ReasonDetail: reason.ToString(),
            detail,
            evidence,
            owner,
            wakeKind,
            wakeCondition,
            ExpectedStepRevision: 0,
            deadline ?? new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero));

    private static WakeManagedTaskWaitCommand Wake(
        string commandId,
        ManagedTaskWaitWakeKind wakeKind,
        string response) =>
        new(
            commandId,
            "run-1",
            "wait-1",
            ExpectedWaitRevision: 0,
            ExpectedStepRevision: 1,
            ExpectedEvidence: "prompt-sha256:6d27",
            wakeKind,
            response,
            ContinuationIntentId: "continuation-1");

    private static ManagedTaskWaitRecord Wait(ManagedTaskWaitWriteResult result)
    {
        Assert.NotNull(result.Wait);
        return result.Wait;
    }

    private static ManagedTaskContinuationIntentRecord Continuation(ManagedTaskWaitWriteResult result)
    {
        Assert.NotNull(result.ContinuationIntent);
        return result.ContinuationIntent;
    }

    private static ManagedTaskContinuationIntentRecord Intent(ManagedTaskContinuationWriteResult result)
    {
        Assert.NotNull(result.ContinuationIntent);
        return result.ContinuationIntent;
    }

    private static ManagedTaskRunSpecification Specification() =>
        new(
            "run-1",
            Input: "{\"release\":\"2026.09\"}",
            InputReference: "git://botnexus@26c7/release.json",
            new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
            new ManagedTaskResourceBounds(12, TimeSpan.FromMinutes(5), 1),
            AuthoredDefinition: null,
            [
                new ManagedTaskStepSpecification(
                    "step-1",
                    "image://build-artifact",
                    new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
                    new ManagedTaskResourceBounds(8, TimeSpan.FromMinutes(2), 1)),
            ]);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }

    private sealed class ThrowingCommitObserver(string commandId, ManagedTaskFlowCommitPoint point)
        : IManagedTaskFlowCommitObserver
    {
        public void OnCommitPoint(ManagedTaskFlowCommitPoint observed, string observedCommandId)
        {
            if (observed == point && observedCommandId == commandId)
                throw new SimulatedManagedTaskWaitCrashException(commandId);
        }
    }

    private sealed class SimulatedManagedTaskWaitCrashException(string commandId)
        : Exception($"Simulated wait-ledger crash for '{commandId}'.");
}
