using BotNexus.Domain.Primitives;
using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Verifies the domain contract for InterruptAndSteerAsync (#799, Part of #704).
/// Phase 1a: contract definition only. Implementation wired in #800.
/// </summary>
public sealed class InterruptAndSteerContractTests
{
    [Fact]
    public void IAgentHandle_HasInterruptAndSteerAsync_Method()
    {
        // Verify the method exists on the interface with the correct signature.
        var method = typeof(IAgentHandle).GetMethod(
            "InterruptAndSteerAsync",
            [typeof(string), typeof(CancellationToken)]);

        method.ShouldNotBeNull("IAgentHandle must declare InterruptAndSteerAsync(string, CancellationToken)");
        method!.ReturnType.ShouldBe(typeof(Task));
    }

    [Fact]
    public void IAgentHandle_InterruptAndSteerAsync_HasOptionalCancellationToken()
    {
        var method = typeof(IAgentHandle).GetMethod(
            "InterruptAndSteerAsync",
            [typeof(string), typeof(CancellationToken)]);

        method.ShouldNotBeNull();
        var ctParam = method!.GetParameters()[1];
        ctParam.ParameterType.ShouldBe(typeof(CancellationToken));
        ctParam.HasDefaultValue.ShouldBeTrue("CancellationToken parameter should be optional (default = default)");
    }

    [Fact]
    public async Task InProcessAgentHandle_InterruptAndSteerAsync_ThrowsNotImplementedException()
    {
        // Phase 1a stub: implementation not yet wired. #800 will replace this with real logic.
        var (_, handle) = CreateHandle();

        await Should.ThrowAsync<NotImplementedException>(
            () => handle.InterruptAndSteerAsync("steer me elsewhere"));
    }

    // ── factory ─────────────────────────────────────────────────────────────────

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
        providers.Register(new StubStreamingProvider());
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
        var handle = new InProcessAgentHandle(agent, AgentId.From("agent-a"), SessionId.From("session-1"), NullLogger.Instance);
        return (agent, handle);
    }

    private sealed class StubStreamingProvider : IApiProvider
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
            var withText = partial with { Content = [new TextContent("ok")] };
            stream.Push(new StartEvent(partial));
            stream.Push(new TextDeltaEvent(0, "ok", withText));
            stream.Push(new DoneEvent(StopReason.Stop, withText));
            return stream;
        }
    }
}
