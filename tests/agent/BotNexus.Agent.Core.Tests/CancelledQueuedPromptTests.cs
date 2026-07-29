using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

using UserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Agent.Core.Tests;

/// <summary>
/// Covers the #2478 pre-flight cancellation guard on the pending follow-up drain.
/// </summary>
/// <remarks>
/// <para>
/// #2458 (#2438) made the gateway boundary enqueue-then-verify-then-reclaim, and the agent loop
/// drains pending follow-ups after a run settles. A follow-up could therefore be dequeued and
/// dispatched AFTER the request that produced it was cancelled, starting a brand new agent loop
/// that nothing holds a cancellation handle for.
/// </para>
/// <para>
/// These tests assert OBSERVABLES, not an internal flag: the provider is never invoked a second
/// time (proving no new loop started), a cancelled/aborted activity IS surfaced (never a silent
/// drop - that is the #2388 defect), and the undelivered follow-up is still reclaimable through
/// the existing #2438 reclaim seam so the boundary can re-deliver it.
/// </para>
/// </remarks>
public sealed class CancelledQueuedPromptTests
{
    /// <summary>
    /// The guard under test: with the run cancelled at the turn boundary, the queued follow-up must
    /// NOT start a second agent loop.
    /// </summary>
    [Fact]
    public async Task CancelledRun_DoesNotStartANewLoopForAQueuedFollowUp()
    {
        var llmCallCount = 0;
        using var provider = RegisterIsolatedProvider((_, _, _) =>
        {
            Interlocked.Increment(ref llmCallCount);
            return TestStreamFactory.CreateTextResponse("first-turn");
        });

        var agent = CreateAgent(provider.Api);
        using var cts = new CancellationTokenSource();
        var queuedFollowUp = new UserMessage("follow-up-after-cancel");

        // Enqueue the follow-up and cancel at the turn boundary - i.e. after the turn's work is
        // done but before the loop reaches the pending-message drain. This is exactly the window
        // the #2438 enqueue-then-verify seam opens.
        using var subscription = agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent is TurnEndEvent)
            {
                agent.FollowUp(queuedFollowUp);
                cts.Cancel();
            }

            return Task.CompletedTask;
        });

        await agent.PromptAsync("start", cts.Token);

        // OBSERVABLE 1: the provider was invoked exactly once. A second invocation would mean a
        // fresh agent loop was started for the cancelled queued message.
        llmCallCount.ShouldBe(1, "the queued follow-up must not start a second agent loop after cancellation");
    }

    /// <summary>
    /// The drop must be loud. A cancelled queued prompt has to surface an aborted activity through
    /// the existing reporting surface, never vanish silently (#2388).
    /// </summary>
    [Fact]
    public async Task CancelledQueuedFollowUp_SurfacesAnAbortedActivity_NotASilentDrop()
    {
        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("first-turn"));

        var agent = CreateAgent(provider.Api);
        using var cts = new CancellationTokenSource();
        var queuedFollowUp = new UserMessage("follow-up-after-cancel");
        var endEvents = new List<AgentEndEvent>();

        using var subscription = agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent is TurnEndEvent)
            {
                agent.FollowUp(queuedFollowUp);
                cts.Cancel();
            }

            if (agentEvent is AgentEndEvent end)
            {
                endEvents.Add(end);
            }

            return Task.CompletedTask;
        });

        var result = await agent.PromptAsync("start", cts.Token);

        // OBSERVABLE 2: an aborted assistant message was produced and an agent_end event carrying
        // it was emitted to listeners. The cancellation is visible to the caller and the channel.
        var aborted = result.OfType<AssistantAgentMessage>().LastOrDefault();
        aborted.ShouldNotBeNull();
        aborted!.FinishReason.ShouldBe(StopReason.Aborted);
        aborted.ErrorMessage.ShouldBe("Operation aborted");

        endEvents.ShouldNotBeEmpty("cancellation must be reported as an agent_end activity, never dropped silently");
        endEvents[^1].Messages
            .OfType<AssistantAgentMessage>()
            .ShouldContain(m => m.FinishReason == StopReason.Aborted);
    }

    /// <summary>
    /// Aborting BEFORE the destructive drain (rather than after) is what keeps the message
    /// recoverable: it is still pending, so the gateway boundary can reclaim and re-deliver it on
    /// the normal send path instead of losing it.
    /// </summary>
    [Fact]
    public async Task CancelledQueuedFollowUp_RemainsReclaimable()
    {
        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("first-turn"));

        var agent = CreateAgent(provider.Api);
        using var cts = new CancellationTokenSource();
        var queuedFollowUp = new UserMessage("follow-up-after-cancel");

        using var subscription = agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent is TurnEndEvent)
            {
                agent.FollowUp(queuedFollowUp);
                cts.Cancel();
            }

            return Task.CompletedTask;
        });

        await agent.PromptAsync("start", cts.Token);

        // OBSERVABLE 3: the message was never consumed by the aborted run, so the #2438 reclaim
        // seam still owns it. If the drain had run under a cancelled token the message would have
        // been removed and then abandoned - unreclaimable and unsent.
        agent.TryReclaimFollowUp(queuedFollowUp)
            .ShouldBeTrue("an undelivered follow-up must stay reclaimable so the boundary can re-deliver it");
    }

    /// <summary>
    /// CONTROL. Without cancellation the follow-up drain must still start the next loop iteration.
    /// This test deliberately stays green under the non-vacuity mutation: it proves the guard is
    /// scoped to cancellation and did not simply disable follow-up dispatch altogether.
    /// </summary>
    [Fact]
    public async Task UncancelledRun_StillDispatchesTheQueuedFollowUp()
    {
        var llmCallCount = 0;
        using var provider = RegisterIsolatedProvider((_, _, _) =>
        {
            Interlocked.Increment(ref llmCallCount);
            return TestStreamFactory.CreateTextResponse("turn");
        });

        var agent = CreateAgent(provider.Api);
        var queuedFollowUp = new UserMessage("follow-up-no-cancel");
        var enqueued = false;

        using var subscription = agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent is TurnEndEvent && !enqueued)
            {
                enqueued = true;
                agent.FollowUp(queuedFollowUp);
            }

            return Task.CompletedTask;
        });

        var result = await agent.PromptAsync("start");

        llmCallCount.ShouldBe(2, "an uncancelled follow-up must still start the next loop iteration");
        result.OfType<UserMessage>().Select(m => m.Content).ShouldContain("follow-up-no-cancel");
        agent.TryReclaimFollowUp(queuedFollowUp).ShouldBeFalse("the loop consumed the follow-up");
    }

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

    private sealed class ProviderRegistration(IDisposable scope, string api) : IDisposable
    {
        public string Api { get; } = api;
        public void Dispose() => scope.Dispose();
    }
}
