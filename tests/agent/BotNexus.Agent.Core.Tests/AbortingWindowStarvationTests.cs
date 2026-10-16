using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using UserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Agent.Core.Tests;

/// <summary>
/// Covers the cron-starvation defect filed upstream as sytone/botnexus#4688: a scheduled turn that
/// arrives while the agent is mid-ABORT is rejected outright instead of being queued, and because
/// every soul/cron job for an agent shares ONE <see cref="Agent"/> instance, that rejection silently
/// drops scheduled work.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentStatus"/> has three states - Idle, Running, and <b>Aborting</b>. The run guard in
/// <c>Agent.RunAsync</c> rejects anything that is NOT Idle, so it throws for both Running and
/// Aborting. The gateway's <c>IAgentHandle.IsRunning</c>, however, is defined as
/// <c>Status == AgentStatus.Running</c> only, so during the abort window the "is it busy?" test
/// answers FALSE while the run guard still throws. Every queue-instead-of-fail seam gated on that
/// predicate therefore falls through precisely when it is needed.
/// </para>
/// <para>
/// These tests assert OBSERVABLES rather than an internal flag: the busy predicate must agree with
/// the run guard, and a prompt issued during the abort window must not be lost.
/// </para>
/// </remarks>
public sealed class AbortingWindowStarvationTests
{
    /// <summary>
    /// The core defect. While the agent is Aborting the run guard still refuses a new run, so any
    /// predicate used to decide "queue vs. send" must report BUSY. Reporting idle here is what lets
    /// a caller take the direct-send branch and eat an InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task AbortingAgent_ReportsBusy_SoCallersQueueInsteadOfThrowing()
    {
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(releaseProvider);

        var agent = CreateAgent(provider.Api);
        using var endGate = HoldAgentEndUntil(agent, releaseProvider.Task);

        var inFlight = agent.PromptAsync("start-long-turn");
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(10))
            .ShouldBeTrue("the turn must be in flight before we abort it");

        var aborting = agent.AbortAsync();
        agent.Status.ShouldBe(
            AgentStatus.Aborting,
            "AbortAsync must flip the agent into the Aborting window (held open by the agent_end gate)");

        agent.IsBusy.ShouldBeTrue(
            "an Aborting agent still rejects new runs, so callers must see it as busy and queue");

        releaseProvider.TrySetResult();
        await aborting;
        try { await inFlight; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The behaviour the busy predicate protects: a turn handed over during the abort window must be
    /// retained and delivered once the agent settles, never silently discarded.
    /// </summary>
    [Fact]
    public async Task PromptQueuedDuringAbortWindow_IsRetainedNotDropped()
    {
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(releaseProvider);

        var agent = CreateAgent(provider.Api);
        using var endGate = HoldAgentEndUntil(agent, releaseProvider.Task);

        var inFlight = agent.PromptAsync("start-long-turn");
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(10))
            .ShouldBeTrue("the turn must be in flight before we abort it");

        var aborting = agent.AbortAsync();
        agent.Status.ShouldBe(
            AgentStatus.Aborting,
            "AbortAsync must flip the agent into the Aborting window (held open by the agent_end gate)");

        var scheduledTurn = new UserMessage("cron-fire-during-abort");
        agent.IsBusy.ShouldBeTrue();
        agent.FollowUp(scheduledTurn);

        releaseProvider.TrySetResult();
        await aborting;
        try { await inFlight; } catch (OperationCanceledException) { }

        agent.TryReclaimFollowUp(scheduledTurn)
            .ShouldBeTrue("a turn queued during the abort window must be retained, not discarded");
    }

    /// <summary>
    /// Non-vacuity guard: proves <c>IsBusy</c> is a real state test and not a constant.
    /// </summary>
    [Fact]
    public async Task IdleAgent_ReportsNotBusy()
    {
        using var provider = RegisterIsolatedProvider((_, _, _) =>
            TestStreamFactory.CreateTextResponse("quick-turn"));

        var agent = CreateAgent(provider.Api);

        agent.IsBusy.ShouldBeFalse("a fresh agent is idle");
        await agent.PromptAsync("turn");
        agent.IsBusy.ShouldBeFalse("a settled agent is idle again");
    }

    /// <summary>
    /// Holds the run inside its <c>agent_end</c> notification until <paramref name="release"/> completes.
    /// The run only returns to Idle in its <c>finally</c> AFTER agent_end listeners finish, so this pins
    /// the agent in <see cref="AgentStatus.Aborting"/> deterministically - without it, the cancelled
    /// run can settle to Idle (even inline, inside <c>cts.Cancel()</c>) before the test observes Aborting.
    /// </summary>
    private static IDisposable HoldAgentEndUntil(Agent agent, Task release) =>
        agent.Subscribe(async (evt, _) =>
        {
            if (evt is AgentEndEvent)
            {
                await release.ConfigureAwait(false);
            }
        });
    private static Agent CreateAgent(string api)
    {
        var options = TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel(api));
        return new Agent(options);
    }

    private static ProviderRegistration RegisterIsolatedProvider(
        Func<LlmModel, Context, SimpleStreamOptions?, LlmStream> factory)
    {
        var api = $"test-api-{Guid.NewGuid():N}";
        var scope = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: factory));
        return new ProviderRegistration(scope, api);
    }

    /// <summary>
    /// Registers a provider whose stream stays open until <paramref name="release"/> is completed,
    /// holding the agent in a real in-flight run WITHOUT blocking a thread-pool thread.
    /// </summary>
    private static ProviderRegistration RegisterBlockingProvider(TaskCompletionSource release)
    {
        var api = $"test-api-{Guid.NewGuid():N}";
        var scope = TestHelpers.RegisterProvider(new TestApiProvider(
            api,
            simpleStreamFactory: (_, _, _) =>
            {
                var stream = new LlmStream();
                _ = Task.Run(async () =>
                {
                    await release.Task.ConfigureAwait(false);
                    var completion = TestStreamFactory.CreateTextResponse("long-turn");
                    await foreach (var evt in completion)
                    {
                        stream.Push(evt);
                    }

                    stream.End(await completion.GetResultAsync().ConfigureAwait(false));
                });
                return stream;
            }));
        return new ProviderRegistration(scope, api);
    }

    private sealed class ProviderRegistration(IDisposable scope, string api) : IDisposable
    {
        public string Api { get; } = api;
        public void Dispose() => scope.Dispose();
    }
}
