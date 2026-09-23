using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Executable contract for the standalone managed-task flow ledger (#4294). The deliberately small
/// API persists state-machine facts; it does not define a workflow language or borrow authority from
/// conversation, cron, memory, or issue-tracker stores.
/// </summary>
public sealed class SqliteManagedTaskFlowLedgerContractTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-managed-task-ledger-").FullName;

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
    public async Task Created_run_steps_and_bounds_are_durable_and_immutable()
    {
        var specification = Specification(
            authored: new("deploy-release", 3, "git://botnexus/definitions/deploy-release.json"));

        await using (var ledger = new SqliteManagedTaskFlowLedger(DbPath))
        {
            var created = await ledger.CreateRunAsync(new("create-1", specification));
            created.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        }

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath);
        var snapshot = await reopened.GetRunAsync("run-1");
        Assert.NotNull(snapshot);
        snapshot.Run.Specification.ShouldBe(specification);
        snapshot.Run.Revision.ShouldBe(0);
        snapshot.Run.Status.ShouldBe(ManagedTaskRunStatus.Pending);
        snapshot.Steps.Select(step => step.Specification).ShouldBe(specification.Steps);
        snapshot.Attempts.ShouldBeEmpty();
        snapshot.Run.CreatedAt.ShouldBe(snapshot.Run.UpdatedAt);

        var duplicate = await reopened.CreateRunAsync(new("create-1", specification));
        duplicate.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Duplicate);

        var conflicting = specification with { Input = "{\"release\":\"different\"}" };
        await Should.ThrowAsync<ManagedTaskLedgerConflictException>(
            async () => await reopened.CreateRunAsync(new("other-command", conflicting)));
    }

    [Fact]
    public async Task Expected_revision_transition_appends_exactly_one_event_and_projection_update_atomically()
    {
        await using var ledger = await CreateLedgerAsync();

        var applied = await ledger.TransitionRunAsync(
            new("command-1", "run-1", ExpectedRevision: 0, ManagedTaskRunStatus.Running));

        applied.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Snapshot(applied).Run.Revision.ShouldBe(1);
        Snapshot(applied).Run.Status.ShouldBe(ManagedTaskRunStatus.Running);
        (await ledger.GetEventsAsync("run-1")).Select(eventRecord => eventRecord.Type)
            .ShouldBe([ManagedTaskEventType.RunCreated, ManagedTaskEventType.RunTransitioned]);

        var stale = await ledger.TransitionRunAsync(
            new("command-2", "run-1", ExpectedRevision: 0, ManagedTaskRunStatus.Completed));
        stale.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.RevisionConflict);
        Snapshot(stale).ShouldBe(Snapshot(applied));
        (await ledger.GetEventsAsync("run-1")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Attempt_epochs_make_duplicate_reordered_and_stale_commands_deterministic()
    {
        await using var ledger = await CreateLedgerAsync();

        var reordered = await ledger.CommitAttemptAsync(new(
            "finish-before-admit", "run-1", "step-1", "attempt-1",
            ExpectedStepRevision: 0, ExpectedAttemptEpoch: 1,
            ManagedTaskAttemptStatus.Succeeded, "result://ok"));
        reordered.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.AttemptNotFound);

        var admitted = await ledger.AdmitAttemptAsync(
            new("admit-1", "run-1", "step-1", ExpectedStepRevision: 0, "attempt-1"));
        admitted.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Snapshot(admitted).Steps.Single().Revision.ShouldBe(1);
        Snapshot(admitted).Attempts.Single().Epoch.ShouldBe(1);

        var duplicate = await ledger.AdmitAttemptAsync(
            new("admit-1", "run-1", "step-1", ExpectedStepRevision: 0, "attempt-1"));
        duplicate.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Duplicate);
        Snapshot(duplicate).ShouldBe(Snapshot(admitted));

        var stale = await ledger.AdmitAttemptAsync(
            new("admit-2", "run-1", "step-1", ExpectedStepRevision: 0, "attempt-2"));
        stale.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.RevisionConflict);

        var futureEpoch = await ledger.CommitAttemptAsync(new(
            "future-epoch", "run-1", "step-1", "attempt-1",
            ExpectedStepRevision: 1, ExpectedAttemptEpoch: 2,
            ManagedTaskAttemptStatus.Failed, ResultReference: null));
        futureEpoch.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.EpochConflict);
        (await ledger.GetEventsAsync("run-1")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Cancellation_is_sticky_and_rejects_new_admission_and_late_terminal_commit()
    {
        await using var ledger = await CreateLedgerAsync();
        var admitted = await ledger.AdmitAttemptAsync(
            new("admit-1", "run-1", "step-1", ExpectedStepRevision: 0, "attempt-1"));

        var cancelled = await ledger.CancelRunAsync(
            new("cancel-1", "run-1", ExpectedRunRevision: 0, "user requested"));
        cancelled.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Snapshot(cancelled).Run.Status.ShouldBe(ManagedTaskRunStatus.Cancelled);
        Snapshot(cancelled).Attempts.Single().Status.ShouldBe(ManagedTaskAttemptStatus.Cancelled);

        var afterCancel = await ledger.AdmitAttemptAsync(new(
            "admit-2", "run-1", "step-1",
            ExpectedStepRevision: Snapshot(cancelled).Steps.Single().Revision, "attempt-2"));
        afterCancel.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Cancelled);

        var lateCommit = await ledger.CommitAttemptAsync(new(
            "late-commit", "run-1", "step-1", "attempt-1",
            ExpectedStepRevision: Snapshot(cancelled).Steps.Single().Revision,
            ExpectedAttemptEpoch: Snapshot(admitted).Attempts.Single().Epoch,
            ManagedTaskAttemptStatus.Succeeded, "result://too-late"));
        lateCommit.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Cancelled);

        var snapshot = await ledger.GetRunAsync("run-1");
        Assert.NotNull(snapshot);
        snapshot.Run.Status.ShouldBe(ManagedTaskRunStatus.Cancelled);
        snapshot.Attempts.Single().Status.ShouldBe(ManagedTaskAttemptStatus.Cancelled);
        snapshot.Attempts.Single().ResultReference.ShouldBeNull();
    }

    [Fact]
    public async Task Unknown_side_effect_is_explicit_terminal_and_not_retryable()
    {
        await using var ledger = await CreateLedgerAsync();
        var admitted = await ledger.AdmitAttemptAsync(
            new("admit-1", "run-1", "step-1", ExpectedStepRevision: 0, "attempt-1"));

        var unknown = await ledger.CommitAttemptAsync(new(
            "unknown-1", "run-1", "step-1", "attempt-1",
            ExpectedStepRevision: 1,
            ExpectedAttemptEpoch: Snapshot(admitted).Attempts.Single().Epoch,
            ManagedTaskAttemptStatus.UnknownSideEffect, ResultReference: null));

        unknown.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        Snapshot(unknown).Attempts.Single().Status.ShouldBe(ManagedTaskAttemptStatus.UnknownSideEffect);
        Snapshot(unknown).Attempts.Single().IsTerminal.ShouldBeTrue();
        Snapshot(unknown).Attempts.Single().IsRetryable.ShouldBeFalse();

        var readmit = await ledger.AdmitAttemptAsync(new(
            "admit-2", "run-1", "step-1", Snapshot(unknown).Steps.Single().Revision, "attempt-2"));
        readmit.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Terminal);
    }

    [Fact]
    public async Task Crash_before_commit_leaves_neither_event_nor_projection_change()
    {
        await using (var setup = await CreateLedgerAsync()) { }

        var observer = new ThrowingCommitObserver(ManagedTaskFlowCommitPoint.BeforeCommit);
        await using (var crashing = new SqliteManagedTaskFlowLedger(DbPath, observer))
        {
            await Should.ThrowAsync<SimulatedManagedTaskLedgerCrashException>(
                async () => await crashing.TransitionRunAsync(
                    new("crash-before", "run-1", 0, ManagedTaskRunStatus.Running)));
        }

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath);
        var snapshot = await reopened.GetRunAsync("run-1");
        Assert.NotNull(snapshot);
        snapshot.Run.Revision.ShouldBe(0);
        snapshot.Run.Status.ShouldBe(ManagedTaskRunStatus.Pending);
        (await reopened.GetEventsAsync("run-1")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Crash_after_commit_is_a_durable_duplicate_on_retry()
    {
        await using (var setup = await CreateLedgerAsync()) { }

        var observer = new ThrowingCommitObserver(ManagedTaskFlowCommitPoint.AfterCommit);
        await using (var crashing = new SqliteManagedTaskFlowLedger(DbPath, observer))
        {
            await Should.ThrowAsync<SimulatedManagedTaskLedgerCrashException>(
                async () => await crashing.TransitionRunAsync(
                    new("crash-after", "run-1", 0, ManagedTaskRunStatus.Running)));
        }

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath);
        var durable = await reopened.GetRunAsync("run-1");
        Assert.NotNull(durable);
        durable.Run.Revision.ShouldBe(1);
        durable.Run.Status.ShouldBe(ManagedTaskRunStatus.Running);
        (await reopened.GetEventsAsync("run-1")).Count.ShouldBe(2);

        var retry = await reopened.TransitionRunAsync(
            new("crash-after", "run-1", 0, ManagedTaskRunStatus.Running));
        retry.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Duplicate);
        (await reopened.GetEventsAsync("run-1")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Schema_version_is_one_and_authored_definition_metadata_is_additive_not_a_workflow_dsl()
    {
        var metadata = new ManagedTaskAuthoredDefinition("deploy-release", 3, "repo://definitions/deploy");
        await using (var ledger = new SqliteManagedTaskFlowLedger(DbPath))
        {
            await ledger.CreateRunAsync(new("create-1", Specification(metadata)));
        }

        SqliteManagedTaskFlowLedger.CurrentSchemaVersion.ShouldBe(1);

        using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        connection.Open();

        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);

        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new List<string>();
        using (var reader = tables.ExecuteReader())
        {
            while (reader.Read())
                names.Add(reader.GetString(0));
        }

        names.ShouldContain("managed_task_run");
        names.ShouldContain("managed_task_step");
        names.ShouldContain("managed_task_attempt");
        names.ShouldContain("managed_task_event");
        names.ShouldAllBe(name =>
            !name.Contains("workflow", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("edge", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("dependency", StringComparison.OrdinalIgnoreCase));

        await using var reopened = new SqliteManagedTaskFlowLedger(DbPath);
        var snapshot = await reopened.GetRunAsync("run-1");
        Assert.NotNull(snapshot);
        snapshot.Run.Specification.AuthoredDefinition.ShouldBe(metadata);
    }

    [Fact]
    public async Task Command_id_reuse_requires_the_exact_same_command_and_run()
    {
        await using var ledger = await CreateLedgerAsync();
        var applied = await ledger.TransitionRunAsync(
            new("shared-command", "run-1", 0, ManagedTaskRunStatus.Running));
        applied.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);

        var duplicate = await ledger.TransitionRunAsync(
            new("shared-command", "run-1", 0, ManagedTaskRunStatus.Running));
        duplicate.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Duplicate);

        await Should.ThrowAsync<ManagedTaskLedgerConflictException>(async () =>
            await ledger.TransitionRunAsync(
                new("shared-command", "run-1", 0, ManagedTaskRunStatus.Completed)));

        var other = Specification(authored: null) with { RunId = "run-2" };
        await Should.ThrowAsync<ManagedTaskLedgerConflictException>(async () =>
            await ledger.CreateRunAsync(new("shared-command", other)));
    }

    [Fact]
    public async Task Terminal_runs_cannot_reopen_admit_or_bypass_cancellation_path()
    {
        await using var ledger = await CreateLedgerAsync();
        await Should.ThrowAsync<ArgumentException>(async () =>
            await ledger.TransitionRunAsync(
                new("bad-cancel", "run-1", 0, ManagedTaskRunStatus.Cancelled)));

        var running = await ledger.TransitionRunAsync(
            new("run", "run-1", 0, ManagedTaskRunStatus.Running));
        var completed = await ledger.TransitionRunAsync(
            new("complete", "run-1", Snapshot(running).Run.Revision, ManagedTaskRunStatus.Completed));

        var reopen = await ledger.TransitionRunAsync(
            new("reopen", "run-1", Snapshot(completed).Run.Revision, ManagedTaskRunStatus.Running));
        reopen.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Terminal);

        var admit = await ledger.AdmitAttemptAsync(
            new("late-admit", "run-1", "step-1", 0, "attempt-1"));
        admit.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Terminal);
    }

    [Fact]
    public async Task Two_ledgers_serialize_stale_writers_at_the_database_fence()
    {
        await using var first = await CreateLedgerAsync();
        await using var second = new SqliteManagedTaskFlowLedger(DbPath);

        var writes = await Task.WhenAll(
            first.TransitionRunAsync(new("writer-1", "run-1", 0, ManagedTaskRunStatus.Running)),
            second.TransitionRunAsync(new("writer-2", "run-1", 0, ManagedTaskRunStatus.Running)));

        writes.Count(result => result.Outcome == ManagedTaskLedgerWriteOutcome.Applied).ShouldBe(1);
        writes.Count(result => result.Outcome == ManagedTaskLedgerWriteOutcome.RevisionConflict).ShouldBe(1);
        Snapshot(writes.Single(result => result.Outcome == ManagedTaskLedgerWriteOutcome.RevisionConflict))
            .Run.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Public_value_lists_are_immutable_value_equal_and_json_round_trip()
    {
        var source = new[]
        {
            new ManagedTaskStepSpecification(
                "step-1", "task://one", new(1, TimeSpan.Zero), new(1, TimeSpan.FromSeconds(1), 1)),
        };
        ManagedTaskValueList<ManagedTaskStepSpecification> values = [source[0]];
        var equal = new ManagedTaskValueList<ManagedTaskStepSpecification>(source);

        values.ShouldBe(equal);
        ((object)values is ICollection<ManagedTaskStepSpecification>).ShouldBeFalse();
        source[0] = source[0] with { StepId = "mutated" };
        values.Single().StepId.ShouldBe("step-1");

        await using var ledger = new SqliteManagedTaskFlowLedger(DbPath);
        var specification = Specification(authored: null) with { Steps = values };
        await ledger.CreateRunAsync(new("create-immutable", specification));
        var persisted = await ledger.GetRunAsync("run-1");
        Assert.NotNull(persisted);
        persisted.Run.Specification.Steps.ShouldBe(values);
    }

    [Fact]
    public async Task Ledger_connections_enable_foreign_keys()
    {
        await using var ledger = new SqliteManagedTaskFlowLedger(DbPath);
        ledger.ForeignKeysEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_run_returns_stable_not_found_without_fabricated_snapshot()
    {
        await using var ledger = new SqliteManagedTaskFlowLedger(DbPath);
        var result = await ledger.TransitionRunAsync(
            new("missing", "unknown-run", 0, ManagedTaskRunStatus.Running));

        result.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.RunNotFound);
        result.Snapshot.ShouldBeNull();
    }

    private static ManagedTaskRunSnapshot Snapshot(ManagedTaskLedgerWriteResult result)
    {
        Assert.NotNull(result.Snapshot);
        return result.Snapshot;
    }

    private async Task<SqliteManagedTaskFlowLedger> CreateLedgerAsync()
    {
        var ledger = new SqliteManagedTaskFlowLedger(DbPath);
        await ledger.CreateRunAsync(new("create-1", Specification(authored: null)));
        return ledger;
    }

    private static ManagedTaskRunSpecification Specification(ManagedTaskAuthoredDefinition? authored) =>
        new(
            "run-1",
            Input: "{\"release\":\"2026.09\"}",
            InputReference: "git://botnexus@26c7/release.json",
            new ManagedTaskPolicyBounds(MaxAttemptsPerStep: 2, CancellationGrace: TimeSpan.FromSeconds(30)),
            new ManagedTaskResourceBounds(MaxTurns: 12, Timeout: TimeSpan.FromMinutes(5), MaxConcurrentAttempts: 1),
            authored,
            [
                new ManagedTaskStepSpecification(
                    "step-1",
                    "image://build-artifact",
                    new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
                    new ManagedTaskResourceBounds(8, TimeSpan.FromMinutes(2), 1)),
            ]);

    private sealed class ThrowingCommitObserver(ManagedTaskFlowCommitPoint point)
        : IManagedTaskFlowCommitObserver
    {
        public void OnCommitPoint(ManagedTaskFlowCommitPoint observed, string commandId)
        {
            if (observed == point)
                throw new SimulatedManagedTaskLedgerCrashException(commandId);
        }
    }

    private sealed class SimulatedManagedTaskLedgerCrashException(string commandId)
        : Exception($"Simulated crash for '{commandId}'.");
}
