using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class CompactionAdmissionTests
{
    [Fact]
    public async Task CompactionWins_RejectsDirectRun_AndQueuedRunWaitsForRelease()
    {
        var (agent, handle) = Create();
        using var lease = handle.TryReserveMaintenance();
        lease.ShouldNotBeNull();
        agent.IsBusy.ShouldBeTrue();
        handle.TryReserveMaintenance().ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => agent.PromptAsync("blocked"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var queued = handle.PromptWhenAvailableAsync(new("queued"), () =>
        {
            started.SetResult();
            return Task.CompletedTask;
        }, cancel.Token);
        started.Task.IsCompleted.ShouldBeFalse();
        await cancel.CancelAsync();
        await Should.ThrowAsync<BotNexus.Gateway.Abstractions.Models.AgentPromptInterruptedException>(async () => await queued);
        lease.Dispose();
        agent.IsBusy.ShouldBeFalse();
        using var next = handle.TryReserveMaintenance();
        next.ShouldNotBeNull();
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task RunWins_ReservationFailsUntilAwaitedRunSettlement()
    {
        var (agent, handle) = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = agent.Subscribe(async (evt, _) =>
        {
            if (evt is AgentStartEvent)
            {
                entered.SetResult();
                await release.Task;
            }
        });
        using var cancel = new CancellationTokenSource();
        var run = agent.PromptAsync("run", cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handle.TryReserveMaintenance().ShouldBeNull();
        await cancel.CancelAsync();
        release.SetResult();
        await run;
        agent.IsBusy.ShouldBeFalse();
        using var lease = handle.TryReserveMaintenance();
        lease.ShouldNotBeNull();
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task DisposedCapturedHandle_CannotAdmitRunAfterReservationRelease()
    {
        var (agent, handle) = Create();
        using var lease = handle.TryReserveMaintenance();
        lease.ShouldNotBeNull();
        await handle.DisposeAsync();
        lease.Dispose();
        handle.TryReserveMaintenance().ShouldBeNull();
        await Should.ThrowAsync<ObjectDisposedException>(() => agent.PromptAsync("stale"));
    }

    [Fact]
    public async Task ReservationRelease_WakesQueuedCoreRun_AndResetCannotMutateReservedState()
    {
        var (agent, handle) = Create();
        using var lease = handle.TryReserveMaintenance();
        lease.ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => agent.Reset());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var run = agent.PromptWhenAvailableAsync(new BotNexus.Agent.Core.Types.UserMessage("queued"), async () =>
        {
            started.SetResult();
            await cancel.CancelAsync();
        }, cancel.Token);
        started.Task.IsCompleted.ShouldBeFalse();
        lease.Dispose();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await run;
        agent.IsBusy.ShouldBeFalse();
        agent.Reset();
        agent.State.Messages.ShouldBeEmpty();
        await handle.DisposeAsync();
    }

    internal static (BotNexus.Agent.Core.Agent Agent, InProcessAgentHandle Handle) Create(SessionId? sessionId = null)
    {
        var model = new LlmModel("test", "test", "test-api", "test", "https://invalid.example",
            false, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024);
        var client = new LlmClient(new ApiProviderRegistry(), new ModelRegistry());
        var agent = new BotNexus.Agent.Core.Agent(new AgentOptions(null, model, client, null, null,
            (_, _) => Task.FromResult<ProviderExecutionOptions?>(null), null, null,
            ToolExecutionMode.Parallel, null, null, new GenerationOptions(), QueueMode.All, QueueMode.All));
        return (agent, new InProcessAgentHandle(agent, AgentId.From("owner"), sessionId ?? SessionId.From("session"), NullLogger.Instance));
    }
}

