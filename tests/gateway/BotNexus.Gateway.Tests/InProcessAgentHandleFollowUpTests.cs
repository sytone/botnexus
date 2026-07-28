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
    public async Task TryFollowUpWhileRunningAsync_NullOrWhitespace_Throws()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, handle) = CreateHandle(release);

        await Should.ThrowAsync<ArgumentException>(() => handle.TryFollowUpWhileRunningAsync("  "));
        release.TrySetResult();
    }

    private static (BotNexus.Agent.Core.Agent Agent, InProcessAgentHandle Handle) CreateHandle(
        TaskCompletionSource release,
        TaskCompletionSource? entered = null)
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
        providers.Register(new BlockingTestProvider(release, entered));
        var llmClient = new LlmClient(providers, modelRegistry);
        var model = modelRegistry.GetModel("test-provider", "test-model")!;

        var options = new AgentOptions(
            InitialState: new AgentInitialState(SystemPrompt: "test", Model: model),
            Model: model,
            LlmClient: llmClient,
            ConvertToLlm: null,
            TransformContext: null,
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Parallel,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
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
    private sealed class BlockingTestProvider(TaskCompletionSource release, TaskCompletionSource? entered) : IApiProvider
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
                if (isFirst)
                {
                    entered?.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                }

                stream.Push(new StartEvent(partial));
                stream.Push(new TextDeltaEvent(0, "hello", withText));
                stream.Push(new DoneEvent(StopReason.Stop, withText));
            });

            return stream;
        }
    }
}
