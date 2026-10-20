using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>Normal action returns must not erase scheduler cancellation (#4102).</summary>
public sealed class CronReturnedCancellationTests
{
    // Named mutation target: removing return-time reconciliation must turn these cases into
    // ok/no_tool_calls. Cancellation happens INSIDE ExecuteAsync, before its completed Task is
    // handed to WhenAny, so the action-completed branch wins deterministically (no timer race).
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scheduler_DeadlineCancelledActionReturnsNormally_RecordsTimedOutNotSuccess(
        bool executionClass, bool reportCost)
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var clock = new DeadlineClock();
        var action = new ReturningAction(reportCost, () => clock.Advance(TimeSpan.FromSeconds(7)));
        var job = Job(executionClass, unlimited: false);
        await store.Store.CreateAsync(job);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(store.Store, services, action, clock);

        var run = await scheduler.RunNowAsync(job.Id);

        action.ReturnedNormally.ShouldBeTrue();
        action.TokenWasCancelled.ShouldBeTrue("the actual action token, not just the fake clock, must be cancelled");
        run.Status.ShouldBe(CronRunStatus.TimedOut);
        run.Error.ShouldBe("Job exceeded 7s timeout");
        var entry = (await store.Store.GetRunHistoryAsync(job.Id)).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.TimedOut);
        entry.Error.ShouldBe(run.Error);
        AssertCost(entry.Cost, reportCost, 7_000);
        var updated = await store.Store.GetAsync(job.Id);
        updated.ShouldNotBeNull();
        updated.LastRunStatus.ShouldBe(CronRunStatus.TimedOut);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scheduler_HostCancelledActionReturnsNormally_RecordsErrorAndRethrows(
        bool unlimited, bool deadlineFirst)
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var clock = new DeadlineClock();
        using var host = new CancellationTokenSource();
        var action = new ReturningAction(true, () =>
        {
            if (deadlineFirst)
                clock.Advance(TimeSpan.FromSeconds(7));
            host.Cancel();
            if (!deadlineFirst)
                clock.Advance(TimeSpan.FromSeconds(7));
        });
        var job = Job(executionClass: true, unlimited);
        await store.Store.CreateAsync(job);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(store.Store, services, action, clock);

        await Should.ThrowAsync<OperationCanceledException>(async () => await scheduler.RunNowAsync(job.Id, host.Token));

        action.ReturnedNormally.ShouldBeTrue();
        action.TokenWasCancelled.ShouldBeTrue();
        var entry = (await store.Store.GetRunHistoryAsync(job.Id)).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Error, "ambient cancellation wins even if the deadline fired first");
        entry.Error.ShouldBe("Cron run aborted before completion.");
        AssertCost(entry.Cost, true, 7_000);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scheduler_OperatorCancelledActionReturnsNormally_RecordsAbortedWithoutAlert(
        bool unlimited, bool deadlineFirst)
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var clock = new DeadlineClock();
        var sink = new RecordingAlertSink();
        var action = new OperatorReturningAction(clock, deadlineFirst);
        var job = Job(executionClass: true, unlimited);
        await store.Store.CreateAsync(job);
        using var services = new ServiceCollection().AddSingleton<ICronFailureAlertSink>(sink).BuildServiceProvider();
        using var scheduler = CreateScheduler(store.Store, services, action, clock);
        action.Cancel = () => scheduler.CancelActiveRunAsync(job.Id);

        var run = await scheduler.RunNowAsync(job.Id);
        var sweep = action.Sweep;
        _ = sweep.ShouldNotBeNull();
        (await sweep).ShouldBe(1);

        action.ReturnedNormally.ShouldBeTrue();
        action.TokenWasCancelled.ShouldBeTrue();
        run.Status.ShouldBe(CronRunStatus.Aborted);
        run.Error.ShouldBe(CronScheduler.OperatorAbortReason);
        var entry = (await store.Store.GetRunHistoryAsync(job.Id)).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Aborted);
        entry.Error.ShouldBe(CronScheduler.OperatorAbortReason);
        AssertCost(entry.Cost, true, 7_000);
        sink.Alerts.ShouldBeEmpty("alerts are enabled, but an intentional operator abort is not a failure alert");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduler_UncancelledNormalReturn_PreservesSuccessAndUnlimitedSentinel(bool unlimited)
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var clock = new DeadlineClock();
        var elapsed = unlimited ? TimeSpan.FromDays(1) : TimeSpan.FromSeconds(3);
        var action = new ReturningAction(false, () => clock.Advance(elapsed));
        var job = Job(executionClass: false, unlimited);
        await store.Store.CreateAsync(job);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(store.Store, services, action, clock);

        var run = await scheduler.RunNowAsync(job.Id);

        action.TokenWasCancelled.ShouldBeFalse();
        run.Status.ShouldBe(CronRunStatus.Ok);
        var entry = (await store.Store.GetRunHistoryAsync(job.Id)).ShouldHaveSingleItem();
        AssertCost(entry.Cost, false, (long)elapsed.TotalMilliseconds);
        clock.CreatedTimers.ShouldBe(unlimited ? 0 : 1, "unlimited must not arm a deadline timer");
    }

    private static CronJob Job(bool executionClass, bool unlimited) =>
        CronStoreTestContext.CreateJob("returned-cancel", actionType: "returning") with
        {
            ExecutionClass = executionClass,
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = unlimited ? 0 : 7 },
            FailureAlertsEnabled = true,
            FailureAlertConversationId = ConversationId.From("cancel-alerts")
        };

    private static CronScheduler CreateScheduler(ICronStore store, ServiceProvider services, ICronAction action, TimeProvider clock) =>
        new(store, [action], services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { DefaultJobTimeoutSeconds = 1 }),
            NullLogger<CronScheduler>.Instance, clock);

    private static void AssertCost(CronRunCost cost, bool reported, long duration)
    {
        cost.DurationMs.ShouldBe(duration);
        cost.TurnCount.ShouldBe(reported ? 3 : null);
        cost.ToolCallCount.ShouldBe(reported ? 0 : null);
        cost.PromptTokens.ShouldBe(reported ? 123 : null);
        cost.CompletionTokens.ShouldBeNull("unknown is not zero, including on cancelled normal returns");
    }

    private sealed class ReturningAction(bool reportCost, Action beforeReturn) : ICronAction
    {
        public string ActionType => "returning";
        public bool ReturnedNormally { get; private set; }
        public bool TokenWasCancelled { get; private set; }

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (reportCost)
                context.RecordCost(new CronRunCost(TurnCount: 3, ToolCallCount: 0, PromptTokens: 123));
            context.RecordToolInvocationCount(0);
            beforeReturn();
            TokenWasCancelled = cancellationToken.IsCancellationRequested;
            ReturnedNormally = true;
            return Task.CompletedTask;
        }
    }

    private sealed class OperatorReturningAction(DeadlineClock clock, bool deadlineFirst) : ICronAction
    {
        public string ActionType => "returning";
        public Func<Task<int>>? Cancel { get; set; }
        public Task<int>? Sweep { get; private set; }
        public bool ReturnedNormally { get; private set; }
        public bool TokenWasCancelled { get; private set; }

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordCost(new CronRunCost(TurnCount: 3, ToolCallCount: 0, PromptTokens: 123));
            context.RecordToolInvocationCount(0);
            if (deadlineFirst)
                clock.Advance(TimeSpan.FromSeconds(7));
            // CancelActiveRunAsync signals synchronously before its observation await. Do not await
            // it inside the action: the sweep is itself waiting for this action/run to finish.
            Sweep = (Cancel ?? throw new InvalidOperationException("Cancel callback not installed"))();
            if (!deadlineFirst)
                clock.Advance(TimeSpan.FromSeconds(7));
            TokenWasCancelled = cancellationToken.IsCancellationRequested;
            ReturnedNormally = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class RecordingAlertSink : ICronFailureAlertSink
    {
        public List<CronFailureAlert> Alerts { get; } = [];
        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    // Only one-shot scheduler deadlines are exercised here. Timers fire synchronously from Advance,
    // including their cancellation callbacks, so no wall-clock delay can decide the tested race.
    private sealed class DeadlineClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<DeadlineTimer> _timers = [];
        public int CreatedTimers { get; private set; }
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            CreatedTimers++;
            var timer = new DeadlineTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }
        private sealed class DeadlineTimer(DeadlineClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                    return false;
                if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
                    throw new NotSupportedException("This clock supports one-shot deadlines only");
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                return true;
            }
            public void FireIfDue()
            {
                if (!_disposed && _due is { } due && due <= clock.GetUtcNow())
                {
                    _due = null;
                    callback(state);
                }
            }
            public void Dispose() { _disposed = true; _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
