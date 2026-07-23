using BotNexus.Domain.Primitives;
using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Isolation;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class InProcessAgentHandleTests
{
    [Fact]
    public async Task SteerAsync_WhenCalled_QueuesSteeringMessage()
    {
        var (agent, handle) = CreateHandle();

        await handle.SteerAsync("adjust behavior");

        agent.HasQueuedMessages.ShouldBeTrue();
    }

    [Fact]
    public async Task FollowUpAsync_WhenCalled_QueuesFollowUpMessage()
    {
        var (agent, handle) = CreateHandle();

        await handle.FollowUpAsync("do this next");

        agent.HasQueuedMessages.ShouldBeTrue();
    }

    [Fact]
    public async Task SteerAsync_WhenAgentIsNotRunning_DoesNotThrow()
    {
        var (agent, handle) = CreateHandle();
        agent.Status.ShouldBe(AgentStatus.Idle);

        Func<Task> act = async () => await handle.SteerAsync("non-blocking steer");

        await act.ShouldNotThrowAsync();
        agent.HasQueuedMessages.ShouldBeTrue();
    }

    [Fact]
    public async Task StreamAsync_EmitsAssistantLifecycleOnly()
    {
        var (_, handle) = CreateHandle();

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in handle.StreamAsync("hello"))
            events.Add(evt);

        events.Count(e => e.Type == AgentStreamEventType.MessageStart).ShouldBe(1);
        events.Count(e => e.Type == AgentStreamEventType.MessageEnd).ShouldBe(1);
        events.Where(e => e.Type == AgentStreamEventType.ContentDelta)
            .Select(e => e.ContentDelta)
            .ShouldContain("hello");
    }

    [Fact]
    public async Task StreamAsync_WithUserMessageContainingImages_PassesImageContentToProvider()
    {
        // This is a real end-to-end test: the image travels from UserMessage →
        // AgentLoopRunner → MessageConverter → provider Context.
        // We capture the Context passed to the provider and assert it has an ImageContent block.
        var capturingProvider = new CapturingStreamingTestProvider();
        var (_, handle) = CreateHandle(capturingProvider);

        var images = new List<AgentImageContent>
        {
            new("data:image/jpeg;base64,/9j/4AAQ==")
        };
        var userMessage = new BotNexus.Agent.Core.Types.UserMessage("describe this image", images);

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in handle.StreamAsync(userMessage))
            events.Add(evt);

        // Verify the provider actually received a call
        capturingProvider.LastContext.ShouldNotBeNull();

        // Verify the UserMessage was converted to a ProviderUserMessage with ImageContent blocks
        var providerMessages = capturingProvider.LastContext!.Messages;
        var userMsg = providerMessages
            .OfType<BotNexus.Agent.Providers.Core.Models.UserMessage>()
            .FirstOrDefault();

        userMsg.ShouldNotBeNull();
        userMsg!.Content.IsText.ShouldBeFalse("image messages use block content, not plain text");
        userMsg.Content.Blocks.ShouldNotBeNull();
        userMsg.Content.Blocks!.OfType<ImageContent>().ShouldHaveSingleItem();

        var imageBlock = userMsg.Content.Blocks.OfType<ImageContent>().Single();
        imageBlock.MimeType.ShouldBe("image/jpeg");
        imageBlock.Data.ShouldBe("/9j/4AAQ==");
    }

    [Fact]
    public async Task PromptAsync_WithUserMessageContainingImages_PassesImageContentToProvider()
    {
        // Non-streaming path: PromptAsync(UserMessage) must also forward images to the provider.
        var capturingProvider = new CapturingStreamingTestProvider();
        var (_, handle) = CreateHandle(capturingProvider);

        var images = new List<AgentImageContent>
        {
            new("https://example.com/diagram.png")
        };
        var userMessage = new BotNexus.Agent.Core.Types.UserMessage("explain the diagram", images);

        var response = await handle.PromptAsync(userMessage);

        response.Content.ShouldNotBeEmpty();
        capturingProvider.LastContext.ShouldNotBeNull();

        var providerMessages = capturingProvider.LastContext!.Messages;
        var userMsg = providerMessages
            .OfType<BotNexus.Agent.Providers.Core.Models.UserMessage>()
            .FirstOrDefault();

        userMsg.ShouldNotBeNull();
        userMsg!.Content.Blocks.ShouldNotBeNull();
        var imageBlock = userMsg.Content.Blocks!.OfType<ImageContent>().SingleOrDefault();
        imageBlock.ShouldNotBeNull();
        // URL-based image: value is used as data, mimeType defaults to image/png
        imageBlock!.Data.ShouldBe("https://example.com/diagram.png");
    }

    [Fact]
    public async Task PromptAsync_RecordsActivity_OnBlockingPath()
    {
        // Regression for #1320: the cron / soul / heartbeat path runs through the
        // blocking PromptAsync overload, which bypasses GatewayHost.ProcessAsync.
        // Before the fix, that path never updated the activity tracker, so the
        // liveness watchdog logged false FATAL "possible deadlock" alerts while
        // cron jobs were actively executing. PromptAsync must record activity.
        var tracker = new ActivityTracker();
        var (_, handle) = CreateHandle(provider: null, activityTracker: tracker);

        // Rewind the tracker so we can prove RecordActivity() moved it forward.
        var before = tracker.LastActivityUtc;
        await Task.Delay(15);

        await handle.PromptAsync("hello");

        tracker.LastActivityUtc.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task StreamAsync_RecordsActivity_PerAgentEvent()
    {
        // The interactive path streams agent events; each one is proof of liveness.
        // The handle's event subscription must record activity so a long-running
        // turn keeps the watchdog's "no activity" window fresh (#1320).
        var tracker = new ActivityTracker();
        var (_, handle) = CreateHandle(provider: null, activityTracker: tracker);

        var before = tracker.LastActivityUtc;
        await Task.Delay(15);

        await foreach (var _ in handle.StreamAsync("hello"))
        {
            // drain the stream; the subscription records activity per event
        }

        tracker.LastActivityUtc.ShouldBeGreaterThan(before);
    }

    // ── #2118 BuildToolCalls correlation ─────────────────────────────────────

    /// <summary>
    /// #2118: BuildToolCalls must correlate each assistant tool-call request (which carries the
    /// arguments) with its tool-result message (which carries result content + error), producing one
    /// full row per call in execution order.
    /// </summary>
    [Fact]
    public void BuildToolCalls_CompletedRun_CorrelatesArgsAndResults_InOrder()
    {
        var messages = new List<AgentMessage>
        {
            new BotNexus.Agent.Core.Types.UserMessage("go"),
            new AssistantAgentMessage(
                "calling",
                ToolCalls:
                [
                    new ToolCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" }),
                    new ToolCallContent("call-2", "write", new Dictionary<string, object?> { ["path"] = "b.txt" })
                ]),
            new ToolResultAgentMessage("call-1", "read",
                new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "file body")]), IsError: false),
            new ToolResultAgentMessage("call-2", "write",
                new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]), IsError: false),
            new AssistantAgentMessage("done")
        };

        var toolCalls = InProcessAgentHandle.BuildToolCalls(messages, pendingToolCallIds: null);

        toolCalls.Count.ShouldBe(2);
        toolCalls[0].ToolCallId.ShouldBe("call-1");
        toolCalls[0].ToolName.ShouldBe("read");
        toolCalls[0].Arguments.ShouldNotBeNull();
        toolCalls[0].Arguments!.ShouldContain("a.txt");
        toolCalls[0].ResultContent.ShouldBe("file body");
        toolCalls[0].IsError.ShouldBeFalse();
        toolCalls[0].IsIncomplete.ShouldBeFalse();

        toolCalls[1].ToolCallId.ShouldBe("call-2");
        toolCalls[1].ResultContent.ShouldBe("ok");
    }

    /// <summary>
    /// #2118: a tool error result must surface IsError = true with the error content preserved.
    /// </summary>
    [Fact]
    public void BuildToolCalls_ErrorResult_FlagsIsError()
    {
        var messages = new List<AgentMessage>
        {
            new AssistantAgentMessage(
                "calling",
                ToolCalls: [new ToolCallContent("call-err", "web_fetch", new Dictionary<string, object?> { ["url"] = "x" })]),
            new ToolResultAgentMessage("call-err", "web_fetch",
                new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "boom")]), IsError: true)
        };

        var toolCalls = InProcessAgentHandle.BuildToolCalls(messages, pendingToolCallIds: null);

        toolCalls.ShouldHaveSingleItem();
        toolCalls[0].IsError.ShouldBeTrue();
        toolCalls[0].ResultContent.ShouldBe("boom");
        toolCalls[0].IsIncomplete.ShouldBeFalse();
    }

    /// <summary>
    /// #2118: a tool call requested but never resulted, whose id is still pending at cancellation, is
    /// flagged IsIncomplete (interrupted mid-flight) with no result content and an error state, so the
    /// interrupted tool is represented consistently in history.
    /// </summary>
    [Fact]
    public void BuildToolCalls_PendingCallWithNoResult_IsInterrupted()
    {
        var messages = new List<AgentMessage>
        {
            new AssistantAgentMessage(
                "calling",
                ToolCalls:
                [
                    new ToolCallContent("call-done", "read", new Dictionary<string, object?> { ["path"] = "a" }),
                    new ToolCallContent("call-inflight", "web_fetch", new Dictionary<string, object?> { ["url"] = "x" })
                ]),
            new ToolResultAgentMessage("call-done", "read",
                new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "content")]), IsError: false)
        };
        var pending = new HashSet<string>(StringComparer.Ordinal) { "call-inflight" };

        var toolCalls = InProcessAgentHandle.BuildToolCalls(messages, pending);

        toolCalls.Count.ShouldBe(2);
        var done = toolCalls.Single(c => c.ToolCallId == "call-done");
        done.IsIncomplete.ShouldBeFalse();
        done.ResultContent.ShouldBe("content");

        var inflight = toolCalls.Single(c => c.ToolCallId == "call-inflight");
        inflight.IsIncomplete.ShouldBeTrue();
        inflight.IsError.ShouldBeTrue();
        inflight.ResultContent.ShouldBeNull();
        inflight.Arguments.ShouldNotBeNull();
        inflight.Arguments!.ShouldContain("x");
    }

    private static (BotNexus.Agent.Core.Agent Agent, InProcessAgentHandle Handle) CreateHandle(
        IApiProvider? provider = null)
        => CreateHandle(provider, activityTracker: null);

    private static (BotNexus.Agent.Core.Agent Agent, InProcessAgentHandle Handle) CreateHandle(
        IApiProvider? provider,
        IActivityTracker? activityTracker)
    {
        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "test-model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));

        var providers = new ApiProviderRegistry();
        providers.Register(provider ?? new StreamingTestProvider());
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
            SessionId: "session-1");

        var agent = new BotNexus.Agent.Core.Agent(options);
        var handle = new InProcessAgentHandle(
            agent,
            AgentId.From("agent-a"),
            SessionId.From("session-1"),
            NullLogger.Instance,
            tools: null,
            resourcesToDispose: null,
            activityTracker: activityTracker);
        return (agent, handle);
    }

    private sealed class StreamingTestProvider : IApiProvider
    {
        public string Api => "test-api";

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context, null);

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
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
            stream.Push(new StartEvent(partial));
            stream.Push(new TextDeltaEvent(0, "hello", withText));
            stream.Push(new DoneEvent(StopReason.Stop, withText));
            return stream;
        }
    }

    /// <summary>
    /// A test provider that captures the last Context it was called with so tests
    /// can assert on the actual provider messages (including ImageContent blocks).
    /// </summary>
    private sealed class CapturingStreamingTestProvider : IApiProvider
    {
        public string Api => "test-api";

        public Context? LastContext { get; private set; }

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context, null);

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            LastContext = context;

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
            var withText = partial with { Content = [new TextContent("ok")] };
            stream.Push(new StartEvent(partial));
            stream.Push(new TextDeltaEvent(0, "ok", withText));
            stream.Push(new DoneEvent(StopReason.Stop, withText));
            return stream;
        }
    }
}
