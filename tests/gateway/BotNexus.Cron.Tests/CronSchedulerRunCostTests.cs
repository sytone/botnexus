using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2641 end-to-end through the scheduler: a run's cost reaches <c>cron_runs</c> via the existing
/// finalization path, on the success path AND on the failure paths.
/// </summary>
/// <remarks>
/// <para>
/// These are the load-bearing tests named by AC7. A mutation that neuters the cost write - passing
/// <c>cost: null</c> at any <c>RecordRunCompleteAsync</c> call site in <c>CronScheduler</c>, or
/// making <c>CronExecutionContext.RecordCost</c> a no-op - compiles cleanly and reddens these tests
/// by name. They deliberately assert on values that no other path produces as a default: the store
/// leaves every cost column NULL unless something wrote it, so a passing assertion here cannot be
/// satisfied by an unrelated default.
/// </para>
/// </remarks>
public sealed class CronSchedulerRunCostTests
{
    [Fact]
    public async Task SuccessfulRun_RecordsCostReportedByTheAction()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-ok", actionType: "costly"));

        var scheduler = CreateScheduler(
            context.Store,
            [new CostReportingAction("costly", new CronRunCost(TurnCount: 5, ToolCallCount: 9, PromptTokens: 17_200, CompletionTokens: 900))]);

        await scheduler.RunNowAsync(JobId.From("job-ok"));

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-ok"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Ok);
        entry.Cost.TurnCount.ShouldBe(5);
        entry.Cost.ToolCallCount.ShouldBe(9);
        entry.Cost.PromptTokens.ShouldBe(17_200);
        entry.Cost.CompletionTokens.ShouldBe(900);
        entry.Cost.TotalTokens.ShouldBe(18_100);
    }

    /// <summary>
    /// AC2: duration is stamped by the SCHEDULER, from data the platform already has, with no
    /// dependence on provider token reporting. The action here reports no duration at all.
    /// </summary>
    [Fact]
    public async Task Run_RecordsDurationStampedByTheScheduler_WithoutTheActionReportingOne()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-duration", actionType: "costly"));

        var scheduler = CreateScheduler(
            context.Store,
            [new CostReportingAction("costly", new CronRunCost(TurnCount: 1))]);

        await scheduler.RunNowAsync(JobId.From("job-duration"));

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-duration"))).ShouldHaveSingleItem();
        entry.Cost.DurationMs.ShouldNotBeNull("the scheduler owns the run clock and must stamp a duration");
        entry.Cost.DurationMs!.Value.ShouldBeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// AC1's named clause, end to end: an action that throws AFTER doing measurable work still has
    /// that work recorded. Without the hoisted execution context in the scheduler's outer catch,
    /// this run would record <c>error</c> with every cost column NULL.
    /// </summary>
    [Fact]
    public async Task FailedRun_StillRecordsCostOfWorkDoneBeforeThrowing()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-throws", actionType: "costly"));

        var scheduler = CreateScheduler(
            context.Store,
            [new ThrowingCostReportingAction("costly", new CronRunCost(TurnCount: 6, ToolCallCount: 14, PromptTokens: 44_000))]);

        var run = await scheduler.RunNowAsync(JobId.From("job-throws"));
        run.Status.ShouldBe(CronRunStatus.Error);

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-throws"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Error);
        entry.Cost.TurnCount.ShouldBe(6);
        entry.Cost.ToolCallCount.ShouldBe(14);
        entry.Cost.PromptTokens.ShouldBe(44_000);
    }

    /// <summary>
    /// A <c>command</c>/<c>webhook</c>-shaped action reports nothing, so its run must stay honestly
    /// unmeasured. Coercing it to zero would rank every shell job on the platform as free - the
    /// inversion AC3 exists to prevent.
    /// </summary>
    [Fact]
    public async Task SilentAction_LeavesTokenColumnsNull_NotZero()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-silent", actionType: "silent"));

        var scheduler = CreateScheduler(context.Store, [new SilentAction("silent")]);
        await scheduler.RunNowAsync(JobId.From("job-silent"));

        var entry = (await context.Store.GetRunHistoryAsync(JobId.From("job-silent"))).ShouldHaveSingleItem();
        entry.Status.ShouldBe(CronRunStatus.Ok);
        entry.Cost.PromptTokens.ShouldBeNull();
        entry.Cost.CompletionTokens.ShouldBeNull();
        entry.Cost.TurnCount.ShouldBeNull();
        entry.Cost.TotalTokens.ShouldBeNull();
    }

    /// <summary>
    /// <c>RecordCost</c> is first-measurement-wins per field: a later report that measured nothing
    /// must not erase an earlier one that did.
    /// </summary>
    [Fact]
    public void RecordCost_LaterUnmeasuredReport_DoesNotEraseEarlierMeasurement()
    {
        var context = new CronExecutionContext
        {
            Job = CronStoreTestContext.CreateJob("job-merge"),
            RunId = RunId.Create(),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = new ServiceCollection().BuildServiceProvider()
        };

        context.RecordCost(new CronRunCost(TurnCount: 3, PromptTokens: 1_000));
        context.RecordCost(new CronRunCost(ToolCallCount: 7));

        context.Cost.TurnCount.ShouldBe(3);
        context.Cost.PromptTokens.ShouldBe(1_000);
        context.Cost.ToolCallCount.ShouldBe(7);
    }

    [Fact]
    public void RecordCost_NegativeValues_AreClampedToZero_NotPropagated()
    {
        var context = new CronExecutionContext
        {
            Job = CronStoreTestContext.CreateJob("job-negative"),
            RunId = RunId.Create(),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = new ServiceCollection().BuildServiceProvider()
        };

        context.RecordCost(new CronRunCost(TurnCount: -4, PromptTokens: -100));

        context.Cost.TurnCount.ShouldBe(0);
        context.Cost.PromptTokens.ShouldBe(0);
    }

    private static CronScheduler CreateScheduler(ICronStore store, IEnumerable<ICronAction> actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretRedactor>(new PassthroughCostRedactor());
        var provider = services.BuildServiceProvider();
        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 }),
            NullLogger<CronScheduler>.Instance);
    }

    /// <summary>An action that completes normally and reports a fixed cost.</summary>
    private sealed class CostReportingAction(string actionType, CronRunCost cost) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordCost(cost);
            return Task.CompletedTask;
        }
    }

    /// <summary>An action that reports a cost and THEN throws, standing in for a run that failed
    /// after doing real, billed work.</summary>
    private sealed class ThrowingCostReportingAction(string actionType, CronRunCost cost) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordCost(cost);
            throw new InvalidOperationException("tool exploded after doing the work");
        }
    }

    /// <summary>An action that reports no cost at all (command / webhook shape).</summary>
    private sealed class SilentAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class PassthroughCostRedactor : ISecretRedactor
    {
        public string Redact(string input) => input;

        public string RedactForExternalDelivery(string input) => input;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;

        public T Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
