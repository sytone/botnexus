using BotNexus.Gateway.Agents;
using BotNexus.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class ManagedTaskWaitCoordinatorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("botnexus-managed-task-runtime-waits-").FullName;
    private string DbPath => Path.Combine(_directory, "ledger.db");

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_directory);
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Best-effort cleanup for transient Windows SQLite handles. */ }
    }

    [Fact]
    public async Task ParkAsync_PersistsWaitAndReturnsWithoutInvokingOwner()
    {
        await using var ledger = await CreateLedgerAsync();
        var owner = new RecordingOwner("agent:release-manager");
        var coordinator = CreateCoordinator(ledger, owner);

        var result = await coordinator.ParkAsync(Park());

        result.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        result.Wait.ShouldNotBeNull();
        result.Wait.Status.ShouldBe(ManagedTaskWaitStatus.Pending);
        owner.Invocations.ShouldBeEmpty();
        (await ledger.GetPendingWaitsAsync()).ShouldHaveSingleItem().WaitId.ShouldBe("wait-1");
    }

    [Fact]
    public async Task WakeAsync_AtomicallyCreatesAndDispatchesContinuationExactlyOnce()
    {
        await using var ledger = await CreateLedgerAsync();
        var owner = new RecordingOwner("agent:release-manager");
        var coordinator = CreateCoordinator(ledger, owner);
        await coordinator.ParkAsync(Park());
        var command = Wake("wake-1");

        var first = await coordinator.WakeAsync(command);
        var duplicate = await coordinator.WakeAsync(command);

        first.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
        duplicate.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Duplicate);
        var invocation = owner.Invocations.ShouldHaveSingleItem();
        invocation.IntentId.ShouldBe("continuation-1");
        invocation.Generation.ShouldBe(1);
        invocation.Response.ShouldBe("ring-2");
        (await ledger.GetPendingContinuationIntentsAsync()).ShouldBeEmpty();
        (await ledger.GetInProgressContinuationIntentsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecoverAndDispatchAsync_AfterRestartReplaysInterruptedGenerationUnderNewFence()
    {
        await using (var first = await CreateLedgerAsync())
        {
            await first.ParkWaitAsync(Park());
            await first.WakeWaitAsync(Wake("wake-before-restart"));
            var claim = await first.ClaimContinuationIntentAsync(new(
                "claim-before-restart", "run-1", "continuation-1", ExpectedGeneration: 0));
            claim.Outcome.ShouldBe(ManagedTaskLedgerWriteOutcome.Applied);
            claim.ContinuationIntent.ShouldNotBeNull();
            claim.ContinuationIntent.Generation.ShouldBe(1);
        }

        await using var restarted = new SqliteManagedTaskFlowLedger(DbPath);
        var owner = new RecordingOwner("agent:release-manager");
        var coordinator = CreateCoordinator(restarted, owner);

        await coordinator.RecoverAndDispatchAsync();

        var invocation = owner.Invocations.ShouldHaveSingleItem();
        invocation.Generation.ShouldBe(3);
        (await restarted.GetPendingContinuationIntentsAsync()).ShouldBeEmpty();
        (await restarted.GetInProgressContinuationIntentsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchPendingAsync_ConcurrentProcessorsInvokeOwnerOnceForClaimedGeneration()
    {
        await using var ledger = await CreateLedgerAsync();
        await ledger.ParkWaitAsync(Park());
        await ledger.WakeWaitAsync(Wake("wake-concurrent"));
        var owner = new BlockingOwner("agent:release-manager");
        var first = CreateCoordinator(ledger, owner);
        var second = CreateCoordinator(ledger, owner);

        var firstDispatch = first.DispatchPendingAsync();
        await owner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await second.DispatchPendingAsync();
        owner.InvocationCount.ShouldBe(1);

        owner.Release.SetResult();
        await firstDispatch;
        owner.InvocationCount.ShouldBe(1);
    }

    private static ManagedTaskWaitCoordinator CreateCoordinator(SqliteManagedTaskFlowLedger ledger, params IManagedTaskContinuationOwner[] owners) =>
        new(ledger, owners, NullLogger<ManagedTaskWaitCoordinator>.Instance);

    private async Task<SqliteManagedTaskFlowLedger> CreateLedgerAsync()
    {
        var ledger = new SqliteManagedTaskFlowLedger(DbPath);
        await ledger.CreateRunAsync(new("create-1", Specification()));
        return ledger;
    }

    private static ParkManagedTaskWaitCommand Park() => new(
        "park-1", "run-1", "step-1", "wait-1", ManagedTaskWaitReason.AskUser,
        "AskUser", "Which deployment ring?", "prompt-sha256:6d27", "agent:release-manager",
        ManagedTaskWaitWakeKind.Answer, "conversation-response:question-17",
        ExpectedStepRevision: 0, DateTimeOffset.UtcNow.AddHours(1));

    private static WakeManagedTaskWaitCommand Wake(string commandId) => new(
        commandId, "run-1", "wait-1", ExpectedWaitRevision: 0, ExpectedStepRevision: 1,
        ExpectedEvidence: "prompt-sha256:6d27", ManagedTaskWaitWakeKind.Answer, "ring-2",
        ContinuationIntentId: "continuation-1");

    private static ManagedTaskRunSpecification Specification() => new(
        "run-1", "{}", "task://input/1",
        new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
        new ManagedTaskResourceBounds(12, TimeSpan.FromMinutes(5), 1), null,
        [new ManagedTaskStepSpecification(
            "step-1", "task://step/1",
            new ManagedTaskPolicyBounds(2, TimeSpan.FromSeconds(30)),
            new ManagedTaskResourceBounds(8, TimeSpan.FromMinutes(2), 1))]);

    private sealed class RecordingOwner(string owner) : IManagedTaskContinuationOwner
    {
        public string Owner => owner;
        public List<ManagedTaskContinuationContext> Invocations { get; } = [];

        public Task ContinueAsync(ManagedTaskContinuationContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(context);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingOwner(string owner) : IManagedTaskContinuationOwner
    {
        public string Owner => owner;
        public int InvocationCount { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ContinueAsync(ManagedTaskContinuationContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
