using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class LocalManagedTaskExecutorTests
{
    [Fact]
    public void AddBotNexusGateway_RegistersManagedTaskExecutorAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();

        services.ShouldNotContain(x => x.ServiceType == typeof(IManagedTaskAttemptStore));
        services.ShouldContain(x => x.ServiceType == typeof(IManagedTaskExecutor));

        using var provider = services.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IManagedTaskExecutor>())
            .Message.ShouldContain(nameof(IManagedTaskAttemptStore));
    }

    [Fact]
    public async Task SubmitAsync_RepeatedAttemptLaunchesExistingExecutorOnce()
    {
        var manager = new RecordingSubAgentManager();
        var store = new InMemoryManagedTaskAttemptStore();
        var executor = new LocalManagedTaskExecutor(manager, store);
        var specification = CreateSpecification();

        var first = await executor.SubmitAsync(specification);
        var second = await executor.SubmitAsync(specification);

        manager.SpawnCount.ShouldBe(1);
        second.AttemptId.ShouldBe(first.AttemptId);
        second.ExecutionId.ShouldBe(first.ExecutionId);
    }

    [Fact]
    public async Task SubmitAsync_ConcurrentExecutorsSharingStoreLaunchOnce()
    {
        var manager = new RecordingSubAgentManager();
        var store = new InMemoryManagedTaskAttemptStore();
        var firstExecutor = new LocalManagedTaskExecutor(manager, store);
        var secondExecutor = new LocalManagedTaskExecutor(manager, store);
        var specification = CreateSpecification();

        await Task.WhenAll(firstExecutor.SubmitAsync(specification), secondExecutor.SubmitAsync(specification));

        manager.SpawnCount.ShouldBe(1);
    }

    [Fact]
    public async Task SubmitAsync_PersistsAttemptBeforeStartingExistingExecutor()
    {
        var events = new List<string>();
        var manager = new RecordingSubAgentManager(() => events.Add("spawn"));
        var store = new RecordingAttemptStore(new InMemoryManagedTaskAttemptStore(), events);
        var executor = new LocalManagedTaskExecutor(manager, store);
        var specification = CreateSpecification();

        await executor.SubmitAsync(specification);

        events.ShouldBe(["persist", "spawn"]);
        manager.LastRequest.ShouldBeSameAs(specification.LocalExecution);
    }

    [Fact]
    public async Task InspectAsync_ReturnsAuthoritativeExecutionIdentityStatusAndResult()
    {
        var manager = new RecordingSubAgentManager();
        var executor = new LocalManagedTaskExecutor(manager, new InMemoryManagedTaskAttemptStore());
        var submitted = await executor.SubmitAsync(CreateSpecification());
        manager.SetTerminal(SubAgentStatus.Completed, "result-ref");

        var receipt = await executor.InspectAsync(submitted.AttemptId);

        receipt.ShouldNotBeNull();
        receipt.ExecutionId.ShouldBe(manager.Info.SubAgentId);
        receipt.Status.ShouldBe(ManagedTaskExecutionStatus.Completed);
        receipt.ResultReference.ShouldBe("result-ref");
    }

    [Fact]
    public async Task CancelAsync_IsStickyAndDoesNotLaunchOrKillTwice()
    {
        var manager = new RecordingSubAgentManager();
        var executor = new LocalManagedTaskExecutor(manager, new InMemoryManagedTaskAttemptStore());
        var submitted = await executor.SubmitAsync(CreateSpecification());

        var first = await executor.CancelAsync(submitted.AttemptId);
        var second = await executor.CancelAsync(submitted.AttemptId);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first.CancellationRequested.ShouldBeTrue();
        second.CancellationRequested.ShouldBeTrue();
        manager.KillCount.ShouldBe(1);
        manager.SpawnCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReconcileAsync_AfterRestartDistinguishesRunningCompletedMissingAndUncertain()
    {
        var store = new InMemoryManagedTaskAttemptStore();
        var manager = new RecordingSubAgentManager();
        var firstExecutor = new LocalManagedTaskExecutor(manager, store);
        var running = await firstExecutor.SubmitAsync(CreateSpecification("running"));
        var completed = await firstExecutor.SubmitAsync(CreateSpecification("completed"));
        var missing = await firstExecutor.SubmitAsync(CreateSpecification("missing"));
        var uncertain = await firstExecutor.SubmitAsync(CreateSpecification("uncertain"));
        manager.SetStatus(running.ExecutionId!, SubAgentStatus.Running);
        manager.SetStatus(completed.ExecutionId!, SubAgentStatus.Completed, "completed-result");
        manager.Remove(missing.ExecutionId!);
        manager.ThrowOnGet(uncertain.ExecutionId!);
        var restarted = new LocalManagedTaskExecutor(manager, store);

        var receipts = await restarted.ReconcileAsync();

        receipts.Single(x => x.AttemptId == running.AttemptId).Status.ShouldBe(ManagedTaskExecutionStatus.Running);
        receipts.Single(x => x.AttemptId == completed.AttemptId).Status.ShouldBe(ManagedTaskExecutionStatus.Completed);
        receipts.Single(x => x.AttemptId == missing.AttemptId).Status.ShouldBe(ManagedTaskExecutionStatus.Missing);
        receipts.Single(x => x.AttemptId == uncertain.AttemptId).Status.ShouldBe(ManagedTaskExecutionStatus.Uncertain);
    }

    [Fact]
    public async Task SubmitAsync_RejectsTimeoutBeyondAbsoluteDeadline()
    {
        var manager = new RecordingSubAgentManager();
        var executor = new LocalManagedTaskExecutor(manager, new InMemoryManagedTaskAttemptStore());
        var specification = CreateSpecification() with { Deadline = DateTimeOffset.UtcNow.AddSeconds(30) };

        var exception = await Should.ThrowAsync<ArgumentException>(() => executor.SubmitAsync(specification));

        exception.Message.ShouldContain("deadline");
        manager.SpawnCount.ShouldBe(0);
    }

    [Fact]
    public async Task SubmitAsync_RequiredContainerOrCapabilityFailsWithoutLocalFallback()
    {
        var manager = new RecordingSubAgentManager();
        var executor = new LocalManagedTaskExecutor(manager, new InMemoryManagedTaskAttemptStore());
        var specification = CreateSpecification() with
        {
            Isolation = ManagedTaskIsolationRequirement.Container,
            RequiredCapabilities = ["gpu"]
        };

        var exception = await Should.ThrowAsync<ManagedTaskExecutionNotSupportedException>(
            () => executor.SubmitAsync(specification));

        exception.Message.ShouldContain("Container");
        manager.SpawnCount.ShouldBe(0);
        var reconciled = await executor.ReconcileAsync();
        reconciled.Single().Status.ShouldBe(ManagedTaskExecutionStatus.Unsupported);
    }

    [Fact]
    public async Task ContainerShapedExecutor_ImplementsSubmitInspectCancelAndReconcileContract()
    {
        IManagedTaskExecutor executor = new ContainerShapedExecutorFake();
        var specification = CreateSpecification() with
        {
            Isolation = ManagedTaskIsolationRequirement.Container,
            RequiredCapabilities = ["container"]
        };

        var submitted = await executor.SubmitAsync(specification);
        var inspected = await executor.InspectAsync(specification.AttemptId);
        var cancelled = await executor.CancelAsync(specification.AttemptId);
        var reconciled = await executor.ReconcileAsync();

        submitted.Status.ShouldBe(ManagedTaskExecutionStatus.Running);
        submitted.ExecutionId.ShouldBe("container-attempt");
        inspected.ShouldNotBeNull();
        inspected.ExecutionId.ShouldBe(submitted.ExecutionId);
        cancelled.ShouldNotBeNull();
        cancelled.CancellationRequested.ShouldBeTrue();
        reconciled.Single().Status.ShouldBe(ManagedTaskExecutionStatus.Cancelled);
    }

    private static ManagedTaskExecutionSpecification CreateSpecification(string attemptId = "attempt-1") => new()
    {
        Version = ManagedTaskExecutionSpecification.CurrentVersion,
        AttemptId = attemptId,
        TaskInputReference = "task://ledger/task-1",
        LocalExecution = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent"),
            ParentSessionId = SessionId.From("parent-session"),
            InheritedConversationId = ConversationId.From("parent-conversation"),
            Task = "Do the bounded work.",
            Mode = new Embody(SubAgentArchetype.Coder),
            MaxTurns = 12,
            TimeoutSeconds = 300,
            ParentToolDenyList = ["dangerous"],
            GrantedPaths = ["Q:/read"],
            GrantedWritePaths = ["Q:/write"]
        },
        Deadline = DateTimeOffset.UtcNow.AddMinutes(10),
        ResourceBounds = new ManagedTaskResourceBounds(12, 300),
        RequiredCapabilities = [],
        OutputContract = "managed-task-result/v1",
        FencingToken = "fence-7"
    };

    private sealed class RecordingAttemptStore(IManagedTaskAttemptStore inner, List<string> events) : IManagedTaskAttemptStore
    {
        public async Task<ManagedTaskAttemptRecord?> GetAsync(string attemptId, CancellationToken ct = default)
            => await inner.GetAsync(attemptId, ct);

        public async Task<IReadOnlyList<ManagedTaskAttemptRecord>> ListAsync(CancellationToken ct = default)
            => await inner.ListAsync(ct);

        public async Task<ManagedTaskAttemptReservation> ReserveAsync(ManagedTaskExecutionSpecification specification, CancellationToken ct = default)
        {
            events.Add("persist");
            return await inner.ReserveAsync(specification, ct);
        }

        public async Task<ManagedTaskAttemptRecord> UpdateAsync(ManagedTaskAttemptRecord record, CancellationToken ct = default)
            => await inner.UpdateAsync(record, ct);
    }

    private sealed class RecordingSubAgentManager(Action? onSpawn = null) : ISubAgentManager
    {
        private readonly Dictionary<string, SubAgentInfo> _records = [];
        private readonly HashSet<string> _throwOnGet = [];
        public int SpawnCount { get; private set; }
        public int KillCount { get; private set; }
        public SubAgentSpawnRequest? LastRequest { get; private set; }
        public SubAgentInfo Info { get; private set; } = null!;

        public Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
        {
            onSpawn?.Invoke();
            SpawnCount++;
            LastRequest = request;
            Info = new SubAgentInfo
            {
                SubAgentId = $"sub-{SpawnCount}",
                ParentSessionId = request.ParentSessionId,
                ChildSessionId = SessionId.From($"child-{SpawnCount}"),
                Task = request.Task,
                Status = SubAgentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow
            };
            _records[Info.SubAgentId] = Info;
            return Task.FromResult(Info);
        }

        public Task<IReadOnlyList<SubAgentInfo>> ListAsync(SessionId parentSessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SubAgentInfo>>(_records.Values.Where(x => x.ParentSessionId == parentSessionId).ToArray());

        public Task<SubAgentInfo?> GetAsync(string subAgentId, CancellationToken ct = default)
        {
            if (_throwOnGet.Contains(subAgentId)) throw new IOException("inspection unavailable");
            return Task.FromResult(_records.GetValueOrDefault(subAgentId));
        }

        public Task<bool> KillAsync(string subAgentId, SessionId requestingSessionId, CancellationToken ct = default)
        {
            KillCount++;
            if (!_records.TryGetValue(subAgentId, out var record)) return Task.FromResult(false);
            _records[subAgentId] = record with { Status = SubAgentStatus.Killed, CompletedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }

        public Task OnCompletedAsync(string subAgentId, string resultSummary, SubAgentRunOutcome? outcome = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public void SetTerminal(SubAgentStatus status, string result) => SetStatus(Info.SubAgentId, status, result);
        public void SetStatus(string id, SubAgentStatus status, string? result = null)
            => _records[id] = _records[id] with { Status = status, ResultSummary = result, CompletedAt = status == SubAgentStatus.Running ? null : DateTimeOffset.UtcNow };
        public void Remove(string id) => _records.Remove(id);
        public void ThrowOnGet(string id) => _throwOnGet.Add(id);
    }

    private sealed class ContainerShapedExecutorFake : IManagedTaskExecutor
    {
        private readonly Dictionary<string, ManagedTaskExecutionReceipt> _receipts = [];

        public Task<ManagedTaskExecutionReceipt> SubmitAsync(ManagedTaskExecutionSpecification specification, CancellationToken ct = default)
        {
            var receipt = _receipts.GetValueOrDefault(specification.AttemptId)
                ?? new ManagedTaskExecutionReceipt(specification.AttemptId, "container-attempt", ManagedTaskExecutionStatus.Running);
            _receipts[specification.AttemptId] = receipt;
            return Task.FromResult(receipt);
        }

        public Task<ManagedTaskExecutionReceipt?> InspectAsync(string attemptId, CancellationToken ct = default)
            => Task.FromResult(_receipts.GetValueOrDefault(attemptId));

        public Task<ManagedTaskExecutionReceipt?> CancelAsync(string attemptId, CancellationToken ct = default)
        {
            if (!_receipts.TryGetValue(attemptId, out var receipt)) return Task.FromResult<ManagedTaskExecutionReceipt?>(null);
            receipt = receipt with { Status = ManagedTaskExecutionStatus.Cancelled, CancellationRequested = true };
            _receipts[attemptId] = receipt;
            return Task.FromResult<ManagedTaskExecutionReceipt?>(receipt);
        }

        public Task<IReadOnlyList<ManagedTaskExecutionReceipt>> ReconcileAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ManagedTaskExecutionReceipt>>(_receipts.Values.ToArray());
    }
}
