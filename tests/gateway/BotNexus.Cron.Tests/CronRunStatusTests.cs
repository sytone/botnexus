using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Covers the <see cref="CronRunStatus"/> constant contract and verifies that every terminal
/// run path (success / error / timeout / host-abort) persists the canonical status value through
/// the shared constant rather than a bare literal. The exact string values are a contract the
/// daily digest, run-history queries, and PR-watch self-reschedule logic depend on — a typo here
/// would silently corrupt run history, which is the failure mode #1631 guards against.
/// </summary>
public sealed class CronRunStatusTests
{
    // The canonical persisted strings. These MUST NOT change without a coordinated migration —
    // history rows and the digest parser compare against these exact values.
    [Theory]
    [InlineData("ok")]
    [InlineData("error")]
    [InlineData("timed_out")]
    [InlineData("running")]
    public void CronRunStatus_ExposesCanonicalValue(string expected)
    {
        var actual = expected switch
        {
            "ok" => CronRunStatus.Ok,
            "error" => CronRunStatus.Error,
            "timed_out" => CronRunStatus.TimedOut,
            "running" => CronRunStatus.Running,
            _ => throw new ArgumentOutOfRangeException(nameof(expected), expected, "unmapped status")
        };

        actual.ShouldBe(expected);
    }

    [Fact]
    public void CronRunStatus_ValuesAreDistinct()
    {
        var all = new[] { CronRunStatus.Ok, CronRunStatus.Error, CronRunStatus.TimedOut, CronRunStatus.Running };
        System.Linq.Enumerable.Distinct(all, StringComparer.Ordinal).Count().ShouldBe(all.Length);
    }

    [Fact]
    public async Task SuccessfulRun_RecordsOkStatus_FromConstant()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe(CronRunStatus.Ok);
        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.LastRunStatus.ShouldBe(CronRunStatus.Ok);
        updated.LastRunError.ShouldBeNull();
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Ok);
    }

    [Fact]
    public async Task FailingRun_RecordsErrorStatus_FromConstant()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new ThrowingAction("test-action", "boom");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe(CronRunStatus.Error);
        run.Error.ShouldBe("boom");
        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.LastRunStatus.ShouldBe(CronRunStatus.Error);
        updated.LastRunError.ShouldNotBeNull();
        updated.LastRunError.ShouldContain("boom");
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Error);
    }

    [Fact]
    public async Task TimedOutRun_RecordsTimedOutStatus_FromConstant()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayedAction("test-action", TimeSpan.FromSeconds(30));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        // 1s default timeout vs a 30s action => guaranteed timeout terminal path.
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 1 };
        var scheduler = CreateScheduler(context.Store, [action], options);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe(CronRunStatus.TimedOut);
        run.Error.ShouldNotBeNull();
        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.LastRunStatus.ShouldBe(CronRunStatus.TimedOut);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.TimedOut);
    }

    [Fact]
    public async Task RunStartedButNotCompleted_IsStampedRunning_FromConstant()
    {
        // RecordRunStartAsync stamps the run "running" before the action executes. A run that is
        // queried before completion must carry the canonical Running value (the abort path relies
        // on this so it can detect and finalize a still-"running" row instead of leaving it stuck).
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);

        var started = await context.Store.RecordRunStartAsync(JobId.From("job-1"));

        started.Status.ShouldBe(CronRunStatus.Running);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe(CronRunStatus.Running);
    }

    [Fact]
    public async Task HostAbortedRun_RecordsErrorStatus_NotStuckRunning()
    {
        // A run aborted via the host cancellation token (gateway shutdown / scheduler stop) must be
        // finalized as Error, never left in Running. This exercises the RecordAbortedRunAsync path
        // whose write-back is folded into FinalizeRunAsync by #1631.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new AbortableAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        using var cts = new CancellationTokenSource();
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"), cts.Token);
        await action.Started.Task; // wait until the action is mid-flight
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await runTask);

        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var entry = history.ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Error, "an aborted run must be finalized as error, not left running");
        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.LastRunStatus.ShouldBe(CronRunStatus.Error);
    }

    // --- helpers (mirrors CronSchedulerTests harness) ---

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IReadOnlyList<ICronAction> actions,
        CronOptions? options = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var monitor = new StaticOptionsMonitor<CronOptions>(
            options ?? new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 });
        return new CronScheduler(store, actions, scopeFactory, monitor, NullLogger<CronScheduler>.Instance);
    }

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public string ActionType { get; } = actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingAction(string actionType, string message) : ICronAction
    {
        public string ActionType { get; } = actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException(message);
    }

    private sealed class DelayedAction(string actionType, TimeSpan delay) : ICronAction
    {
        public string ActionType { get; } = actionType;
        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken)
            => await Task.Delay(delay, cancellationToken);
    }

    private sealed class AbortableAction(string actionType) : ICronAction
    {
        public string ActionType { get; } = actionType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
