using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.Messages;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Agent.Providers.IntegrationMock;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using ProviderUserMessage = BotNexus.Agent.Providers.Core.Models.UserMessage;

namespace BotNexus.Agent.Core.Tests.Loop;

[Collection(ApiProviderRegistryCollection.Name)]
public sealed class ToolCapacityProviderIdentityTests
{
    private sealed class ReceiptTool : IAgentTool
    {
        public Action? OnExecuted { get; init; }
        public string Name => "identity_receipt";
        public string Label => Name;
        public Tool Definition => new(Name, "receipt", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            OnExecuted?.Invoke();
            return Task.FromResult(new AgentToolResult([]));
        }
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("completions")]
    [InlineData("responses-sse")]
    [InlineData("responses-websocket")]
    public async Task Loop_RealCopilotHeadersPreserveOriginAcrossResultContinuationAndRetry(string transport)
    {
        var api = "capacity-header-" + transport;
        var requests = new List<Context>();
        var headers = new List<string>();
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (model, context, _) =>
        {
            requests.Add(context);
            var messages = transport == "messages" ? context.Messages : MessageTransformer.TransformMessages(context.Messages, model);
            headers.Add(CopilotHeaders.BuildDynamicHeaders(messages, false)["X-Initiator"]);
            if (requests.Count == 1) return TestStreamFactory.CreateToolCallResponse(("one", "identity_receipt", new Dictionary<string, object?>()));
            if (requests.Count == 2) throw new BotNexus.Agent.Providers.Core.ProviderRateLimitException("retry", 429, TimeSpan.FromMilliseconds(1));
            return TestStreamFactory.CreateTextResponse("done");
        }));
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            ProviderMessageTransformer = DefaultProviderMessageTransformer.TransformAsync,
            ProviderRequestContextProvider = (context, _) => Task.FromResult<string?>($"arbitrary host section used={context.ToolCapacity.Used}")
        };
        await AgentLoopRunner.RunAsync([new AgentUserMessage("real input")], new AgentContext("stable", [], [new ReceiptTool()]), config, _ => Task.CompletedTask, CancellationToken.None);
        headers.ShouldBe(new[] { "user", "agent", "agent" });
        foreach (var request in requests)
        {
            var section = request.Messages.Where(m => m.IsRequestOnlyContext).ShouldHaveSingleItem().ShouldBeOfType<ProviderUserMessage>();
            section.Content.Text.ShouldNotBeNull().ShouldStartWith("arbitrary host section");
            request.Messages.OfType<ProviderUserMessage>().Where(m => !m.IsRequestOnlyContext).Select(m => m.Content.Text).ShouldBe(new[] { "real input" });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loop_RealIntegrationMockCatalogUsesOriginalKeyWithInitialAndToolResultInput(bool continuation)
    {
        var provider = new IntegrationMockProvider(new MockCatalogLoader(new MockCatalog(new Dictionary<string, IReadOnlyList<ScriptedResponseStep>>
        {
            ["ORIGINAL_KEY"] = [new("text_delta", Delta: "original script selected"), new("text_end"), new("done")]
        })));
        var registry = new ApiProviderRegistry();
        registry.Register(provider);
        var model = TestHelpers.CreateTestModel(IntegrationMockModels.ApiName) with { Provider = IntegrationMockModels.ProviderName };
        var requests = new List<ProviderRequestContext>();
        var config = TestHelpers.CreateTestConfig(model: model) with
        {
            LlmClient = new LlmClient(registry, new ModelRegistry()),
            ProviderMessageTransformer = DefaultProviderMessageTransformer.TransformAsync,
            ProviderRequestContextProvider = (context, _) =>
            {
                requests.Add(context);
                return Task.FromResult<string?>("arbitrary host advisory, not a script key");
            }
        };
        IReadOnlyList<AgentMessage> history = continuation
            ? [new AgentUserMessage("ORIGINAL_KEY"), new AssistantAgentMessage("", ToolCalls: [new ToolCallContent("prior", "identity_receipt", new Dictionary<string, object?>())], FinishReason: StopReason.ToolUse), new ToolResultAgentMessage("prior", "identity_receipt", new AgentToolResult([]))]
            : [];
        var messages = continuation
            ? await AgentLoopRunner.ContinueAsync(new AgentContext("stable", history, []), config, _ => Task.CompletedTask, CancellationToken.None)
            : await AgentLoopRunner.RunAsync([new AgentUserMessage("ORIGINAL_KEY")], new AgentContext("stable", [], []), config, _ => Task.CompletedTask, CancellationToken.None);
        messages.OfType<AssistantAgentMessage>().Last().Content.ShouldBe("original script selected");
        requests.ShouldHaveSingleItem().ToolCapacity.Used.ShouldBe(0L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleTurn_DirectAndAgentProjectFreshCapacityWithoutToolsOrTranscriptPollution(bool throughAgent)
    {
        var api = "capacity-single-" + throughAgent;
        var requests = new List<Context>();
        var snapshots = new List<ToolCapacitySnapshot>();
        var observations = new List<ToolCapacityObservation>();
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, options) =>
        {
            requests.Add(context);
            options.ShouldNotBeNull().ToolChoiceMode.ShouldBe(ToolChoice.None);
            return TestStreamFactory.CreateTextResponse("done");
        }));
        var model = TestHelpers.CreateTestModel(api);
        ProviderRequestContextProvider renderer = (context, _) =>
        {
            snapshots.Add(context.ToolCapacity);
            return Task.FromResult<string?>("single host advisory");
        };
        IReadOnlyList<AgentMessage> messages;
        if (throughAgent)
        {
            var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(new AgentInitialState(SystemPrompt: "stable", Model: model, Messages: [], Tools: [new ReceiptTool()]), model) with
            { MaxToolResults = 1, ProviderRequestContextProvider = renderer, ToolCapacityObserver = observations.Add });
            messages = await agent.PromptWithoutToolsAsync("real input");
            agent.State.Messages.OfType<AgentUserMessage>().Select(m => m.Content).ShouldBe(new[] { "real input" });
        }
        else
            messages = await AgentLoopRunner.RunSingleProviderTurnAsync(new AgentUserMessage("real input"), new AgentContext("stable", [], [new ReceiptTool()]),
                TestHelpers.CreateTestConfig(model: model) with { MaxToolResults = 1, ProviderRequestContextProvider = renderer, ToolCapacityObserver = observations.Add }, _ => Task.CompletedTask, CancellationToken.None);
        var snapshot = snapshots.ShouldHaveSingleItem();
        snapshot.Used.ShouldBe(0L);
        snapshot.Remaining.ShouldBe(1L);
        snapshot.Phase.ShouldBe(ToolCapacityPhase.Executing);
        observations.ShouldHaveSingleItem().Stage.ShouldBe(ToolCapacityStage.Start);
        var request = requests.ShouldHaveSingleItem();
        request.SystemPrompt.ShouldBe("stable");
        request.Messages.Where(m => m.IsRequestOnlyContext).ShouldHaveSingleItem().ShouldBeOfType<ProviderUserMessage>().Content.Text.ShouldBe("single host advisory");
        (request.Tools?.Count ?? 0).ShouldBe(0);
        request.Messages.OfType<ProviderUserMessage>().Select(m => m.Content.Text).ShouldBe(new[] { "real input", "single host advisory" });
        messages.OfType<AgentUserMessage>().Select(m => m.Content).ShouldBe(new[] { "real input" });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SingleTurn_RendererFailureOrCancellationNeverInvokesProvider(bool throughAgent, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var api = $"capacity-single-failure-{throughAgent}-{cancel}";
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
        { requests++; return TestStreamFactory.CreateTextResponse("must not run"); }));
        var model = TestHelpers.CreateTestModel(api);
        ProviderRequestContextProvider renderer = (_, _) =>
        {
            if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            throw new InvalidOperationException("renderer failed");
        };
        if (throughAgent)
        {
            var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: model) with { ProviderRequestContextProvider = renderer });
            await agent.PromptWithoutToolsAsync("real input", cancellationToken: cancellation.Token);
            agent.State.LastCompletion.ShouldNotBeNull().Status.ShouldBe(cancel ? BotNexus.Agent.Core.ExtensionPoints.RunCompletion.RunCompletionStatus.Cancelled : BotNexus.Agent.Core.ExtensionPoints.RunCompletion.RunCompletionStatus.Failed);
        }
        else if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => AgentLoopRunner.RunSingleProviderTurnAsync(new AgentUserMessage("real input"), new AgentContext(null, [], []), TestHelpers.CreateTestConfig(model: model) with { ProviderRequestContextProvider = renderer }, _ => Task.CompletedTask, cancellation.Token));
        else
            await Should.ThrowAsync<InvalidOperationException>(() => AgentLoopRunner.RunSingleProviderTurnAsync(new AgentUserMessage("real input"), new AgentContext(null, [], []), TestHelpers.CreateTestConfig(model: model) with { ProviderRequestContextProvider = renderer }, _ => Task.CompletedTask, cancellation.Token));
        requests.ShouldBe(0);
    }

    [Fact]
    public void RequestOnlyMetadata_PreservesWireTextButDoesNotSerializeIdentityOrReplaceConversation()
    {
        var host = new ProviderUserMessage(new UserMessageContent("arbitrary visible host content"), 1) { IsRequestOnlyContext = true };
        Message message = host;
        var json = JsonSerializer.Serialize(message);
        json.ShouldContain("arbitrary visible host content");
        json.ShouldNotContain("IsRequestOnlyContext");
        (host with { Timestamp = 2 }).IsRequestOnlyContext.ShouldBeTrue();
        CopilotHeaders.InferInitiator([host]).ShouldBe("agent");
        CopilotHeaders.InferInitiator([host, new ProviderUserMessage(new UserMessageContent("new real input"), 2)]).ShouldBe("user");
        IntegrationMockProvider.ExtractKey(new Context(null, [host])).ShouldBe(string.Empty);
        IntegrationMockProvider.ExtractKey(new Context(null, [new ProviderUserMessage(new UserMessageContent("ORIGINAL_KEY"), 0), host])).ShouldBe("ORIGINAL_KEY");
    }

    private sealed class RecordingMockProvider(IntegrationMockProvider inner) : IApiProvider
    {
        public List<Context> Requests { get; } = [];
        public string Api => inner.Api;
        public BotNexus.Agent.Providers.Core.Streaming.LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        {
            Requests.Add(context);
            return inner.Stream(model, context, options);
        }
        public BotNexus.Agent.Providers.Core.Streaming.LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            Requests.Add(context);
            return inner.StreamSimple(model, context, options);
        }
    }

    [Fact]
    public async Task Loop_RealMockToolChainKeepsOriginalCatalogKeyAndOneTypedSection()
    {
        var scripts = new Dictionary<string, IReadOnlyList<ScriptedResponseStep>>
        {
            ["ORIGINAL_KEY"] = [new("tool_call", ToolName: "identity_receipt", ToolCallId: "one", ToolArguments: new Dictionary<string, object?>()), new("done", StopReason: "toolUse")]
        };
        var provider = new RecordingMockProvider(new IntegrationMockProvider(new MockCatalogLoader(new MockCatalog(scripts))));
        var registry = new ApiProviderRegistry();
        registry.Register(provider);
        var model = TestHelpers.CreateTestModel(IntegrationMockModels.ApiName) with { Provider = IntegrationMockModels.ProviderName };
        var tool = new ReceiptTool { OnExecuted = () => scripts["ORIGINAL_KEY"] = [new("text_delta", Delta: "chain completed"), new("text_end"), new("done")] };
        var messages = await AgentLoopRunner.RunAsync([new AgentUserMessage("ORIGINAL_KEY")], new AgentContext("stable", [], [tool]),
            TestHelpers.CreateTestConfig(model: model) with
            {
                LlmClient = new LlmClient(registry, new ModelRegistry()),
                ProviderMessageTransformer = DefaultProviderMessageTransformer.TransformAsync,
                ProviderRequestContextProvider = (context, _) => Task.FromResult<string?>($"arbitrary chain advisory used={context.ToolCapacity.Used}")
            }, _ => Task.CompletedTask, CancellationToken.None);
        messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().ToolCallId.ShouldBe("one");
        messages.OfType<AssistantAgentMessage>().Last().Content.ShouldBe("chain completed");
        provider.Requests.Count.ShouldBe(2);
        provider.Requests.Select(IntegrationMockProvider.ExtractKey).ShouldBe(new[] { "ORIGINAL_KEY", "ORIGINAL_KEY" });
        for (var i = 0; i < provider.Requests.Count; i++)
        {
            var request = provider.Requests[i];
            request.SystemPrompt.ShouldBe("stable");
            var section = request.Messages.Where(m => m.IsRequestOnlyContext).ShouldHaveSingleItem().ShouldBeOfType<ProviderUserMessage>();
            section.Content.Text.ShouldBe($"arbitrary chain advisory used={i}");
        }
        provider.Requests[1].Messages.OfType<ToolResultMessage>().ShouldHaveSingleItem().ToolCallId.ShouldBe("one");
    }
}
