using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression coverage for #3545: a manual run accepted by the cron tool belongs to the scheduler,
/// not to the tool call that requested it.
/// </summary>
public sealed class CronManualRunAcceptanceTests
{
    [Fact]
    public async Task CronTool_Run_ReturnsAcceptedRunBeforeTheActionCompletes()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: action.ActionType));
        var scheduler = CreateScheduler(context.Store, action);
        var tool = new CronTool(context.Store, scheduler, AgentId.From("agent-a"));

        var invocation = tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "run",
            ["jobId"] = "job-1"
        });

        await action.Started.Task;
        try
        {
            invocation.IsCompleted.ShouldBeTrue("cron.run must acknowledge the trigger without awaiting the action");
            var payload = JsonDocument.Parse(ReadText(await invocation)).RootElement;
            payload.GetProperty("jobId").GetString().ShouldBe("job-1");
            payload.GetProperty("runId").GetString().ShouldNotBeNullOrWhiteSpace();
            payload.GetProperty("status").GetString().ShouldBe("accepted");
            payload.GetProperty("pollWith").GetString().ShouldBe("cron history");
        }
        finally
        {
            action.Release();
        }

        await TestAwait.EventuallyAsync(
            async () => (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).Single().Status == CronRunStatus.Ok,
            "the accepted run to complete successfully");
    }

    [Fact]
    public async Task CancellingTheToolCallAfterAcceptance_DoesNotCancelTheRun()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: action.ActionType));
        var scheduler = CreateScheduler(context.Store, action);
        var tool = new CronTool(context.Store, scheduler, AgentId.From("agent-a"));
        using var caller = new CancellationTokenSource();

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "run",
            ["jobId"] = "job-1"
        }, caller.Token);
        ReadText(result).ShouldContain("accepted");
        await action.Started.Task;

        await caller.CancelAsync();
        action.ObservedToken.IsCancellationRequested.ShouldBeFalse(
            "the accepted run must no longer be linked to the tool-call deadline");

        action.Release();
        await TestAwait.EventuallyAsync(
            async () => (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).Single().Status == CronRunStatus.Ok,
            "the accepted run to complete successfully");
    }

    [Fact]
    public async Task AcceptedRun_RemainsCancellableThroughTheOperatorPath()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new BlockingAction();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1", actionType: action.ActionType));
        var scheduler = CreateScheduler(context.Store, action);
        var tool = new CronTool(context.Store, scheduler, AgentId.From("agent-a"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "run",
            ["jobId"] = "job-1"
        });
        await action.Started.Task;

        (await scheduler.CancelActiveRunAsync(JobId.From("job-1"))).ShouldBe(1);
        await TestAwait.EventuallyAsync(
            async () =>
            {
                var run = (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).Single();
                return run.Status == CronRunStatus.Aborted && run.Error == CronScheduler.OperatorAbortReason;
            },
            "the accepted run to record the operator abort");
    }

    private static CronScheduler CreateScheduler(ICronStore store, ICronAction action)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new CronScheduler(
            store,
            [action],
            services.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;

    private sealed class BlockingAction : ICronAction
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ActionType => "blocking-action";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
