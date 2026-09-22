using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Evaluations;
using BotNexus.Gateway.Abstractions.Evaluations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.PostRunEvaluations;

public sealed class PostRunEvaluationCoordinatorTests
{
    [Fact]
    public async Task Enqueue_SlowEvaluator_ReturnsBeforeEvaluatorCompletes()
    {
        var started = Signal();
        var release = Signal();
        var evaluator = new DelegateEvaluator("slow", async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return PostRunEvaluationResult.Passed();
        });
        using var coordinator = Create([evaluator], capacity: 2);
        await coordinator.StartAsync(CancellationToken.None);

        coordinator.Enqueue(Snapshot("run-slow")).ShouldBe(PostRunEvaluationAdmission.Accepted);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.Task.IsCompleted.ShouldBeFalse();

        release.TrySetResult();
        await evaluator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enqueue_DuplicateRunIdentity_ExecutesOnce()
    {
        var release = Signal();
        var evaluator = new DelegateEvaluator("once", async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return PostRunEvaluationResult.Passed();
        });
        using var coordinator = Create([evaluator], capacity: 2);
        await coordinator.StartAsync(CancellationToken.None);
        var snapshot = Snapshot("run-duplicate");

        coordinator.Enqueue(snapshot).ShouldBe(PostRunEvaluationAdmission.Accepted);
        coordinator.Enqueue(snapshot).ShouldBe(PostRunEvaluationAdmission.Duplicate);
        release.TrySetResult();
        await evaluator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        evaluator.InvocationCount.ShouldBe(1);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enqueue_WhenQueueIsFull_ReturnsSaturated()
    {
        var firstStarted = Signal();
        var release = Signal();
        var evaluator = new DelegateEvaluator("blocked", async cancellationToken =>
        {
            firstStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return PostRunEvaluationResult.Passed();
        });
        using var coordinator = Create([evaluator], capacity: 1);
        await coordinator.StartAsync(CancellationToken.None);

        coordinator.Enqueue(Snapshot("run-first")).ShouldBe(PostRunEvaluationAdmission.Accepted);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Enqueue(Snapshot("run-buffered")).ShouldBe(PostRunEvaluationAdmission.Accepted);
        coordinator.Enqueue(Snapshot("run-saturated")).ShouldBe(PostRunEvaluationAdmission.Saturated);

        release.TrySetResult();
        await evaluator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enqueue_ConcurrentDuplicateRunIdentity_ExecutesOnce()
    {
        var release = Signal();
        var evaluator = new DelegateEvaluator("concurrent", async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return PostRunEvaluationResult.Passed();
        });
        using var coordinator = Create([evaluator], capacity: 8);
        await coordinator.StartAsync(CancellationToken.None);
        var snapshot = Snapshot("run-concurrent");

        var admissions = Enumerable.Range(0, 32)
            .AsParallel()
            .Select(_ => coordinator.Enqueue(snapshot))
            .ToArray();

        admissions.Count(result => result == PostRunEvaluationAdmission.Accepted).ShouldBe(1);
        admissions.Count(result => result == PostRunEvaluationAdmission.Duplicate).ShouldBe(31);
        release.TrySetResult();
        await evaluator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evaluator.InvocationCount.ShouldBe(1);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enqueue_AfterCompletedRun_RemainsDuplicate()
    {
        var evaluator = new DelegateEvaluator("completed", _ =>
            ValueTask.FromResult(PostRunEvaluationResult.Passed()));
        using var coordinator = Create([evaluator], capacity: 2);
        await coordinator.StartAsync(CancellationToken.None);
        var snapshot = Snapshot("run-completed");

        coordinator.Enqueue(snapshot).ShouldBe(PostRunEvaluationAdmission.Accepted);
        await evaluator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Enqueue(snapshot).ShouldBe(PostRunEvaluationAdmission.Duplicate);
        evaluator.InvocationCount.ShouldBe(1);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EvaluatorTimeoutAndFailure_DoNotPreventLaterEvaluator()
    {
        var never = Signal();
        var laterRan = Signal();
        var timedOut = new DelegateEvaluator("timeout", async cancellationToken =>
        {
            await never.Task.WaitAsync(cancellationToken);
            return PostRunEvaluationResult.Passed();
        });
        var failed = new DelegateEvaluator("failure", _ => throw new InvalidOperationException("boom"));
        var later = new DelegateEvaluator("later", _ =>
        {
            laterRan.TrySetResult();
            return ValueTask.FromResult(PostRunEvaluationResult.Passed("ok"));
        });
        using var coordinator = Create([timedOut, failed, later], capacity: 2, timeout: TimeSpan.FromMilliseconds(25));
        await coordinator.StartAsync(CancellationToken.None);

        coordinator.Enqueue(Snapshot("run-isolation")).ShouldBe(PostRunEvaluationAdmission.Accepted);
        await laterRan.Task.WaitAsync(TimeSpan.FromSeconds(5));

        failed.InvocationCount.ShouldBe(1);
        later.InvocationCount.ShouldBe(1);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NonCooperativeEvaluatorTimeout_DoesNotPreventLaterEvaluator()
    {
        var neverCompletes = Signal();
        var laterRan = Signal();
        var nonCooperative = new DelegateEvaluator("non-cooperative", async _ =>
        {
            await neverCompletes.Task;
            return PostRunEvaluationResult.Passed();
        });
        var later = new DelegateEvaluator("later-after-timeout", _ =>
        {
            laterRan.TrySetResult();
            return ValueTask.FromResult(PostRunEvaluationResult.Passed());
        });
        using var coordinator = Create(
            [nonCooperative, later],
            capacity: 2,
            timeout: TimeSpan.FromMilliseconds(25));
        await coordinator.StartAsync(CancellationToken.None);

        coordinator.Enqueue(Snapshot("run-non-cooperative")).ShouldBe(PostRunEvaluationAdmission.Accepted);
        await laterRan.Task.WaitAsync(TimeSpan.FromSeconds(5));

        later.InvocationCount.ShouldBe(1);
        neverCompletes.TrySetResult();
        await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopAsync_CancelsInFlightEvaluatorAndIsBounded()
    {
        var started = Signal();
        var cancellationObserved = Signal();
        var evaluator = new DelegateEvaluator("cancel", async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
            return PostRunEvaluationResult.Passed();
        });
        using var coordinator = Create([evaluator], capacity: 1, timeout: TimeSpan.FromMinutes(1));
        await coordinator.StartAsync(CancellationToken.None);
        coordinator.Enqueue(Snapshot("run-stop")).ShouldBe(PostRunEvaluationAdmission.Accepted);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static PostRunEvaluationCoordinator Create(
        IReadOnlyList<IPostRunEvaluator> evaluators,
        int capacity,
        TimeSpan? timeout = null)
        => new(
            evaluators,
            new PostRunEvaluationOptions
            {
                Capacity = capacity,
                PerEvaluatorTimeout = timeout ?? TimeSpan.FromSeconds(5),
                MaxResultDetailBytes = 32,
                DeduplicationCapacity = Math.Max(16, capacity + 1)
            },
            NullLogger<PostRunEvaluationCoordinator>.Instance);

    private static RunOutcomeSnapshot Snapshot(string runId) => RunOutcomeSnapshot.Create(
        RunId.From(runId),
        SessionId.From("session-1"),
        ConversationId.From("conversation-1"),
        AgentId.From("agent-1"),
        DateTimeOffset.UtcNow,
        assistantContent: "done",
        completion: null,
        usage: null,
        tools: []);

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateEvaluator(
        string id,
        Func<CancellationToken, ValueTask<PostRunEvaluationResult>> evaluate) : IPostRunEvaluator
    {
        private int _invocationCount;

        public PostRunEvaluatorDescriptor Descriptor { get; } = new(
            PostRunEvaluatorId.From(id),
            new PostRunEvaluatorVersion(1, 0));

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public TaskCompletionSource Completed { get; } = Signal();

        public async ValueTask<PostRunEvaluationResult> EvaluateAsync(
            RunOutcomeSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            try
            {
                return await evaluate(cancellationToken);
            }
            finally
            {
                Completed.TrySetResult();
            }
        }
    }
}
