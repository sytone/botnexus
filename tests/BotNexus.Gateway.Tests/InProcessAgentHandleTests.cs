using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class InProcessAgentHandleTests
{
    [Fact]
    public async Task SteerAsync_WhenCalled_QueuesSteeringMessage()
    {
        var (agent, handle) = CreateHandle();

        await handle.SteerAsync("adjust behavior");

        agent.HasQueuedMessages.Should().BeTrue();
    }

    [Fact]
    public async Task FollowUpAsync_WhenCalled_QueuesFollowUpMessage()
    {
        var (agent, handle) = CreateHandle();

        await handle.FollowUpAsync("do this next");

        agent.HasQueuedMessages.Should().BeTrue();
    }

    [Fact]
    public async Task SteerAsync_WhenAgentIsNotRunning_DoesNotThrow()
    {
        var (agent, handle) = CreateHandle();
        agent.Status.Should().Be(AgentStatus.Idle);

        var act = async () => await handle.SteerAsync("non-blocking steer");

        await act.Should().NotThrowAsync();
        agent.HasQueuedMessages.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_EmitsAssistantLifecycleOnly()
    {
        var (_, handle) = CreateHandle();

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in handle.StreamAsync("hello"))
            events.Add(evt);

        events.Count(e => e.Type == AgentStreamEventType.MessageStart).Should().Be(1);
        events.Count(e => e.Type == AgentStreamEventType.MessageEnd).Should().Be(1);
        events.Where(e => e.Type == AgentStreamEventType.ContentDelta)
            .Select(e => e.ContentDelta)
            .Should()
            .Contain("hello");
    }

    private static (BotNexus.Agent.Core.Agent Agent, InProcessAgentHandle Handle) CreateHandle()
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
        providers.Register(new StreamingTestProvider());
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
        var handle = new InProcessAgentHandle(agent, "agent-a", "session-1", NullLogger.Instance);
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
}
