using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #4283: an action that ignores cancellation after its timeout must be detached from the scheduler
/// tick without surrendering its scope or same-job execution ownership until it really exits.
/// </summary>
public sealed class CronUncooperativeActionIsolationTests
{
    [Fact]
    public async Task TimedOutUncooperativeAction_DoesNotBlockTickOrUnrelatedDueJobs()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var stuck = new UncooperativeAction("stuck-action");
        var unrelated = new CountingAction("unrelated-action");
        await SeedDueJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1);
        await SeedDueJobAsync(store, "command", unrelated.ActionType);
        await SeedDueJobAsync(store, "noop", unrelated.ActionType);
        var scheduler = CreateScheduler(store.Store, [stuck, unrelated]);

        await InvokeProcessTickAsync(scheduler).WaitAsync(TimeSpan.FromSeconds(5));

        stuck.ExecutionCount.ShouldBe(1);
        unrelated.ExecutionCount.ShouldBe(2, "both unrelated due jobs must execute after the stuck run times out");
        var timedOut = (await store.Store.GetRunHistoryAsync(JobId.From("stuck"))).ShouldHaveSingleItem();
        timedOut.Status.ShouldBe(CronRunStatus.TimedOut);
        timedOut.CompletedAt.ShouldNotBeNull("the abandoned run must receive a terminal result promptly");

        stuck.Release();
    }

    [Fact]
    public async Task LaterTick_SkipsQuarantinedJobWithoutReentry_AndStillRunsUnrelatedJob()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var stuck = new UncooperativeAction("stuck-action");
        var unrelated = new CountingAction("unrelated-action");
        await SeedDueJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1);
        await SeedDueJobAsync(store, "unrelated", unrelated.ActionType);
        var scheduler = CreateScheduler(store.Store, [stuck, unrelated]);

        await InvokeProcessTickAsync(scheduler).WaitAsync(TimeSpan.FromSeconds(5));
        await store.Store.SetNextRunAtAsync(JobId.From("stuck"), DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.Store.SetNextRunAtAsync(JobId.From("unrelated"), DateTimeOffset.UtcNow.AddMinutes(-1));

        await InvokeProcessTickAsync(scheduler).WaitAsync(TimeSpan.FromSeconds(2));

        stuck.ExecutionCount.ShouldBe(1, "the quarantined action must not overlap itself");
        unrelated.ExecutionCount.ShouldBe(2, "a later scheduler tick must remain live for unrelated jobs");
        var stuckHistory = await store.Store.GetRunHistoryAsync(JobId.From("stuck"));
        stuckHistory.Count.ShouldBe(2, "the suppressed due occurrence must be durable rather than log-only");
        stuckHistory.ShouldContain(run =>
            run.Status == CronRunStatus.Skipped &&
            run.Error != null &&
            run.Error.Contains("overlapping execution", StringComparison.OrdinalIgnoreCase));

        stuck.Release();
    }

    [Fact]
    public async Task ManualRepeat_WhileTimedOutActionIsQuarantined_FailsClosedWithoutOverlap()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var stuck = new UncooperativeAction("stuck-action");
        await SeedDueJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1);
        var scheduler = CreateScheduler(store.Store, [stuck]);

        var first = await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(TimeSpan.FromSeconds(5));
        var repeat = await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(TimeSpan.FromSeconds(2));

        first.Status.ShouldBe(CronRunStatus.TimedOut);
        repeat.Status.ShouldBe(CronRunStatus.Skipped);
        stuck.ExecutionCount.ShouldBe(1, "manual repeat must fail closed while the abandoned action still owns the job");
        var history = await store.Store.GetRunHistoryAsync(JobId.From("stuck"));
        history.Count.ShouldBe(2);
        history.ShouldContain(run => run.Id == repeat.Id && run.Status == CronRunStatus.Skipped);

        stuck.Release();
    }

    [Fact]
    public async Task ConcurrentManualRepeat_QueuedBeforeTimeout_FailsClosedWithoutOverlap()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var stuck = new UncooperativeAction("stuck-action");
        await SeedDueJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1);
        var scheduler = CreateScheduler(store.Store, [stuck]);

        var first = scheduler.RunNowAsync(JobId.From("stuck"));
        await stuck.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var repeat = scheduler.RunNowAsync(JobId.From("stuck"));

        (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status.ShouldBe(CronRunStatus.TimedOut);
        (await repeat.WaitAsync(TimeSpan.FromSeconds(2))).Status.ShouldBe(CronRunStatus.Skipped);
        stuck.ExecutionCount.ShouldBe(1, "a trigger queued before quarantine publication must not start later");

        stuck.Release();
    }

    [Fact]
    public async Task TimedOutUncooperativeAction_KeepsScopeAliveUntilActionActuallyFinishes()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var disposal = new DisposalProbe();
        var services = new ServiceCollection()
            .AddScoped(_ => disposal)
            .BuildServiceProvider();
        var stuck = new ScopeObservingUncooperativeAction("stuck-action");
        await SeedDueJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1);
        var scheduler = CreateScheduler(store.Store, [stuck], services.GetRequiredService<IServiceScopeFactory>());

        var run = await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(TimeSpan.FromSeconds(5));

        run.Status.ShouldBe(CronRunStatus.TimedOut);
        disposal.IsDisposed.ShouldBeFalse("the abandoned action can still use services from its execution scope");

        stuck.Release();
        await disposal.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disposal.IsDisposed.ShouldBeTrue();
    }

    private static async Task SeedDueJobAsync(
        CronStoreTestContext store,
        string id,
        string actionType,
        int timeoutSeconds = 30)
    {
        var job = CronStoreTestContext.CreateJob(id, actionType: actionType) with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = timeoutSeconds }
        };
        await store.Store.CreateAsync(job);
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IReadOnlyList<ICronAction> actions,
        IServiceScopeFactory? scopeFactory = null)
    {
        scopeFactory ??= new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            actions,
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(new CronOptions
            {
                Enabled = true,
                TickIntervalSeconds = 1,
                DefaultJobTimeoutSeconds = 30,
                MaxConcurrentJobs = 1
            }),
            NullLogger<CronScheduler>.Instance);
    }

    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class CountingAction(string actionType) : ICronAction
    {
        private int _executionCount;
        public string ActionType => actionType;
        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executionCount);
            return Task.CompletedTask;
        }
    }

    private class UncooperativeAction(string actionType) : ICronAction
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executionCount;

        public string ActionType => actionType;
        public int ExecutionCount => Volatile.Read(ref _executionCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();

        public virtual async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executionCount);
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
    }

    private sealed class ScopeObservingUncooperativeAction(string actionType) : UncooperativeAction(actionType)
    {
        public override async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            _ = context.Services.GetRequiredService<DisposalProbe>();
            await base.ExecuteAsync(context, cancellationToken);
        }
    }

    private sealed class DisposalProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            IsDisposed = true;
            Disposed.TrySetResult();
        }
    }
}
