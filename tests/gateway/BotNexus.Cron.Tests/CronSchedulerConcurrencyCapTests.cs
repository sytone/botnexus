using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2670: Phase 2 of the scheduler tick fans every due job out concurrently. These tests pin the
/// aggregate bound (<see cref="CronOptions.MaxConcurrentJobs"/>) and prove it is independent of the
/// per-job <c>_jobLocks</c> serialisation.
/// </summary>
public sealed class CronSchedulerConcurrencyCapTests
{
    [Fact]
    public async Task Tick_NeverExceedsMaxConcurrentJobs_AndStillRunsEveryDueJob()
    {
        const int jobCount = 12;
        const int cap = 3;

        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new ConcurrencyProbeAction("test-action", TimeSpan.FromMilliseconds(40));

        for (var i = 0; i < jobCount; i++)
        {
            var job = CronStoreTestContext.CreateJob($"job-{i}", actionType: "test-action") with
            {
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await context.Store.CreateAsync(job);
        }

        var scheduler = CreateScheduler(
            context.Store,
            [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = cap });

        await InvokeProcessTickAsync(scheduler);

        // A cap that silently drops jobs would be a worse defect than unbounded fan-out.
        action.ExecutionCount.ShouldBe(jobCount, "every due job must still run under the cap");
        action.PeakConcurrency.ShouldBeLessThanOrEqualTo(cap,
            $"observed peak concurrency {action.PeakConcurrency} exceeded the configured cap of {cap}");
    }

    [Fact]
    public void Options_ExposeADocumentedDefaultCap()
    {
        new CronOptions().MaxConcurrentJobs.ShouldBe(CronOptions.DefaultMaxConcurrentJobs);
        CronOptions.DefaultMaxConcurrentJobs.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Tick_WithNonPositiveCap_FallsBackToDefaultAndStillRunsEveryDueJob(int configuredCap)
    {
        const int jobCount = 6;

        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new ConcurrencyProbeAction("test-action", TimeSpan.FromMilliseconds(20));

        for (var i = 0; i < jobCount; i++)
        {
            var job = CronStoreTestContext.CreateJob($"job-{i}", actionType: "test-action") with
            {
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await context.Store.CreateAsync(job);
        }

        var scheduler = CreateScheduler(
            context.Store,
            [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = configuredCap });

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(jobCount);
        action.PeakConcurrency.ShouldBeLessThanOrEqualTo(CronOptions.DefaultMaxConcurrentJobs,
            "a non-positive cap must degrade to the documented default, not to unbounded fan-out");
    }

    [Fact]
    public async Task PerJobSerialisation_IsUnchanged_ByTheAggregateCap()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new ConcurrencyProbeAction("test-action", TimeSpan.FromMilliseconds(60));
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: "test-action"));

        // A generous aggregate cap means the ONLY thing that can keep these two same-job runs from
        // overlapping is the per-job lock. If _jobLocks were folded into the aggregate bound this
        // would go red.
        var scheduler = CreateScheduler(
            context.Store,
            [action],
            new CronOptions { Enabled = true, TickIntervalSeconds = 1, MaxConcurrentJobs = 16 });

        var first = scheduler.RunNowAsync(JobId.From("job-1"));
        var second = scheduler.RunNowAsync(JobId.From("job-1"));
        await Task.WhenAll(first, second);

        action.ExecutionCount.ShouldBe(2);
        action.PeakConcurrency.ShouldBe(1, "two runs of the SAME job must never overlap");
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        CronOptions options)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            actions,
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(options),
            NullLogger<CronScheduler>.Instance);
    }

    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// Records the high-water mark of simultaneously in-flight executions.
    /// </summary>
    private sealed class ConcurrencyProbeAction(string actionType, TimeSpan hold) : ICronAction
    {
        private int _current;
        private int _peak;
        private int _executions;

        public string ActionType => actionType;
        public int PeakConcurrency => Volatile.Read(ref _peak);
        public int ExecutionCount => Volatile.Read(ref _executions);

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _current);
            int observedPeak;
            while (now > (observedPeak = Volatile.Read(ref _peak)))
            {
                if (Interlocked.CompareExchange(ref _peak, now, observedPeak) == observedPeak)
                    break;
            }

            try
            {
                await Task.Delay(hold, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _executions);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }
}
