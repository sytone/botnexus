using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;
using AgentCoreUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Covers <see cref="InProcessAgentHandle.TryFollowUpWhileRunningAsync"/> against a REAL
/// <see cref="BotNexus.Agent.Core.Agent"/> driven by a provider that blocks mid-turn, so the
/// running/idle decision is exercised over genuine run state rather than a mocked flag (#2438).
/// </summary>
/// <remarks>
/// All coordination uses deterministic <see cref="TaskCompletionSource"/> gates - no sleeps and
/// no timing assumptions. Every test ends in an unconditional assertion.
/// </remarks>
public sealed class InProcessAgentHandleFollowUpTests
{
    [Fact]
    public async Task TryFollowUpWhileRunningAsync_WhenIdle_ReturnsFalseAndDoesNotQueue()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (agent, handle) = CreateHandle(release);

        var queued = await handle.TryFollowUpWhileRunningAsync("later");

        // An idle agent's follow-up queue is never drained again, so queueing here would strand
        // the message. The caller must be told to send it normally instead.
        queued.ShouldBeFalse();
        agent.HasQueuedMessages.ShouldBeFalse();
        release.TrySetResult();
    }

    [Fact]
    public async Task TryFollowUpWhileRunningAsync_WhileRunning_QueuesAndIsConsumedAfterRunSettles()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (agent, handle) = CreateHandle(release, entered);

        var run = agent.PromptAsync("start");
        await entered.Task;

        var queued = await handle.TryFollowUpWhileRunningAsync("follow me");

        queued.ShouldBeTrue();
        agent.HasQueuedMessages.ShouldBeTrue();

        release.TrySetResult();
        var produced = await run;

        // The follow-up is injected as a user message that drives the continuation, and is no
        // longer pending afterwards.
        produced.OfType<AgentCoreUserMessage>().Select(m => m.Content).ShouldContain("follow me");
        agent.HasQueuedMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task TryFollowUpWhileRunningAsync_WhileRunning_DoesNotThrowAgentAlreadyRunning()
    {
        // The whole point: a follow-up against a busy agent must not take the PromptAsync path
        // and trip Agent.RunAsync's single-turn guard (the #2388 message-loss exception).
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (agent, handle) = CreateHandle(release, entered);

        var run = agent.PromptAsync("start");
        await entered.Task;

        var queued = await handle.TryFollowUpWhileRunningAsync("no throw");

        queued.ShouldBeTrue();
        release.TrySetResult();
        await run;
        // Reaching here without an InvalidOperationException IS the assertion, plus:
        agent.Status.ShouldBe(AgentStatus.Idle);
    }

    [Fact]
    public async Task TryFollowUpWhileRunningAsync_WhenQueueFull_ThrowsRatherThanDroppingSilently()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (agent, handle) = CreateHandle(release, entered);
        agent.FollowUpQueueCapacity = 1;

        var run = agent.PromptAsync("start");
        await entered.Task;

        (await handle.TryFollowUpWhileRunningAsync("first")).ShouldBeTrue();

        await Should.ThrowAsync<PendingMessageQueueFullException>(
            () => handle.TryFollowUpWhileRunningAsync("second"));

        release.TrySetResult();
        await run;
    }

    [Fact]
    public async Task TryFollowUpWhileRunningAsync_WhileAborting_DoesNotQueueAndHandsBackAfterIdle()
    {
        // #4688 / #4731 review: an aborting run exits before its follow-up drain (#2388) and goes
        // Idle without touching the queue, so a follow-up accepted during Aborting is stranded
        // while the caller believes it was delivered. It must instead be handed back (false) once
        // the agent is idle, so the caller can send it as a fresh prompt.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (agent, handle) = CreateHandle(release, entered);

        var run = agent.PromptAsync("start");
        await entered.Task;

        var abort = agent.AbortAsync();
        agent.Status.ShouldBe(AgentStatus.Aborting);

        var followUp = handle.TryFollowUpWhileRunningAsync("after abort");

        release.TrySetResult();
        await abort;
        try { await run; } catch (OperationCanceledException) { }

        var queued = await followUp;

        queued.ShouldBeFalse();
        agent.Status.ShouldBe(AgentStatus.Idle);
        agent.HasQueuedMessages.ShouldBeFalse();

        // The caller's fresh dispatch now succeeds - no "Agent is already running.".
        var produced = await agent.PromptAsync("after abort");
        produced.OfType<AgentCoreUserMessage>().Select(m => m.Content).ShouldContain("after abort");
    }

    [Fact]
    public async Task TryFollowUpWhileRunningAsync_AbortBetweenPostEnqueueReads_IsReclaimedNotReportedQueued()
    {
        // #4731 review P1: the post-enqueue code read Status and then, separately, IsBusy. An abort
        // landing between the two reads made IsBusy answer true for a run that exits before its
        // drain, so the message was reported queued and never delivered. The seam injects the
        // abort exactly inside that window.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (agent, handle) = CreateHandle(release, entered);

        var run = agent.PromptAsync("start");
        await entered.Task;

        Task? abort = null;
        handle.AfterFollowUpEnqueuedForTest = () =>
        {
            abort = agent.AbortAsync();
            agent.Status.ShouldBe(AgentStatus.Aborting);
            // Let the cancelled run settle; the follow-up path must wait for it, not race it.
            release.TrySetResult();
        };

        var queued = await handle.TryFollowUpWhileRunningAsync("raced");
        await abort.ShouldNotBeNull();
        try { await run; } catch (OperationCanceledException) { }

        queued.ShouldBeFalse("observing Aborting must never report queued-success");
        agent.HasQueuedMessages.ShouldBeFalse("the message must be reclaimed, not stranded in the aborted run's queue");
        agent.Status.ShouldBe(AgentStatus.Idle);
    }

    [Fact]
    public async Task TryFollowUpWhileRunningAsync_AbortExactlyBetweenStatusAndBusyReads_IsNotReportedQueued()
    {
        // #4731 review P1 (exact interleaving): the old code read Status (Running), then IsBusy.
        // An abort landing between those reads made IsBusy true -> "queued" against a run that
        // exits before its drain. The seam fires precisely in that window.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (agent, handle) = CreateHandle(release, entered);

        var run = agent.PromptAsync("start");
        await entered.Task;

        Task? abort = null;
        handle.BeforeLifecycleSnapshotForTest = () =>
        {
            abort = agent.AbortAsync();
            release.TrySetResult();
        };

        var queued = await handle.TryFollowUpWhileRunningAsync("between reads");
        await abort.ShouldNotBeNull();
        try { await run; } catch (OperationCanceledException) { }

        queued.ShouldBeFalse("an abort observed by the lifecycle decision must not yield queued-success");
        agent.HasQueuedMessages.ShouldBeFalse("the message must be reclaimed, not stranded");
    }

    [Fact]
    public async Task TryFollowUpWhileRunningAsync_CallerCancelsWhileWaitingForAbort_ReclaimsTheMessage()
    {
        // #4731 review P2 / #4971: cancelling the stream reader can unwind the run even while
        // the provider is blocked. Hold the awaited agent_end listener instead: RunAsync cannot
        // complete its active-run task (the real WaitForIdleAsync boundary) until it returns.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (agent, handle) = CreateHandle(release, entered, providerSettled);
        using var subscription = agent.Subscribe(async (@event, _) =>
        {
            if (@event is AgentEndEvent)
            {
                ending.TrySetResult();
                await releaseEnd.Task.ConfigureAwait(false);
            }
        });

        using var cts = new CancellationTokenSource();
        Task? abort = null;
        Task<bool>? followUp = null;
        var run = agent.PromptAsync("start");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handle.AfterFollowUpEnqueuedForTest = () => abort = agent.AbortAsync();

            followUp = handle.TryFollowUpWhileRunningAsync("orphan?", cts.Token);
            await ending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            abort.ShouldNotBeNull().IsCompleted.ShouldBeFalse("the listener must hold abort settlement");
            run.IsCompleted.ShouldBeFalse("the active run must remain unsettled");
            agent.Status.ShouldBe(AgentStatus.Aborting);
            followUp.IsCompleted.ShouldBeFalse("the follow-up must be waiting for the abort to settle");
            agent.HasQueuedMessages.ShouldBeTrue("the caller's message must be pending before cancellation");

            // Equal content, different identity: cancellation must reclaim only this caller's
            // instance, not clear the queue or remove another producer's message.
            var otherMessage = new AgentCoreUserMessage("orphan?");
            agent.FollowUp(otherMessage);

            await cts.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(
                () => followUp.WaitAsync(TimeSpan.FromSeconds(5)));

            agent.TryReclaimFollowUp(otherMessage).ShouldBeTrue("another producer's exact message must remain queued");
            agent.HasQueuedMessages.ShouldBeFalse("a cancelled wait must reclaim the message rather than leave it ownerless");
            abort.ShouldNotBeNull().IsCompleted.ShouldBeFalse("caller cancellation must not release the abort gate");
            run.IsCompleted.ShouldBeFalse("reclamation must finish before the aborted run settles");
        }
        finally
        {
            // Release both independent gates even if readiness or an assertion fails. Cancel
            // and observe the follow-up too, then join BOTH lifecycle tasks unconditionally.
            release.TrySetResult();
            releaseEnd.TrySetResult();
            try
            {
                await cts.CancelAsync();
                if (followUp is not null)
                {
                    try { await followUp.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                try
                {
                    await Task.WhenAll(abort ?? agent.AbortAsync(), run, providerSettled.Task).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public async Task TryFollowUpWhileRunningAsync_NullOrWhitespace_Throws()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, handle) = CreateHandle(release);

        await Should.ThrowAsync<ArgumentException>(() => handle.TryFollowUpWhileRunningAsync("  "));
        release.TrySetResult();
    }

    private static (BotNexus.Agent.Core.Agent Agent, InProcessAgentHandle Handle) CreateHandle(
        TaskCompletionSource release,
        TaskCompletionSource? entered = null,
        TaskCompletionSource? providerSettled = null)
    {
        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "Test Model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));

        var providers = new ApiProviderRegistry();
        providers.Register(new BlockingTestProvider(release, entered, providerSettled));
        var llmClient = new LlmClient(providers, modelRegistry);
        var model = modelRegistry.GetModel("test-provider", "test-model")!;

        var options = new AgentOptions(
            InitialState: new AgentInitialState(SystemPrompt: "test", Model: model),
            Model: model,
            LlmClient: llmClient,
            ProviderMessageTransformer: null,
            AgentContextTransformer: null,
            ProviderExecutionOptionsProvider: (_, _) => Task.FromResult<ProviderExecutionOptions?>(null),
            SteeringMessageProvider: null,
            FollowUpMessageProvider: null,
            ToolExecutionMode: ToolExecutionMode.Parallel,
            ToolExecutionPolicy: null,
            ToolResultTransformer: null,
            GenerationSettings: new GenerationOptions(),
            SteeringMode: QueueMode.All,
            FollowUpMode: QueueMode.All,
            SessionId: "session-followup");

        var agent = new BotNexus.Agent.Core.Agent(options);
        var handle = new InProcessAgentHandle(
            agent,
            AgentId.From("agent-a"),
            SessionId.From("session-followup"),
            NullLogger.Instance);
        return (agent, handle);
    }

    /// <summary>
    /// Provider that signals when the first turn has started and then holds it open until the
    /// test releases it, giving a deterministic in-flight window. Subsequent turns (the
    /// follow-up continuation) complete immediately.
    /// </summary>
    private sealed class BlockingTestProvider(
        TaskCompletionSource release,
        TaskCompletionSource? entered,
        TaskCompletionSource? providerSettled) : IApiProvider
    {
        private int _calls;

        public string Api => "test-api";

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context, null);

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var isFirst = Interlocked.Increment(ref _calls) == 1;
            var stream = new LlmStream();
            var partial = new AssistantMessage(
                Content: [],
                Api: model.Api,
                Provider: model.Provider,
                ModelId: model.Id,
                Usage: Usage.Empty(),
                StopReason: StopReason.Stop,
                ErrorMessage: null,
                ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var withText = partial with { Content = [new TextContent("hello")] };

            _ = Task.Run(async () =>
            {
                try
                {
                    if (isFirst)
                    {
                        entered?.TrySetResult();
                        await release.Task.ConfigureAwait(false);
                    }

                    stream.Push(new StartEvent(partial));
                    stream.Push(new TextDeltaEvent(0, "hello", withText));
                    stream.Push(new DoneEvent(StopReason.Stop, withText));
                    if (isFirst)
                        providerSettled?.TrySetResult();
                }
                catch (Exception error)
                {
                    if (isFirst && providerSettled is not null)
                        providerSettled.TrySetException(error);
                    else
                        throw;
                }
            });

            return stream;
        }
    }
}
