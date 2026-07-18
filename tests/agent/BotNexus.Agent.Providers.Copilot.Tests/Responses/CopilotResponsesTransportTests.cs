using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Copilot.Tests.Responses;

public sealed class CopilotResponsesTransportTests
{
    [Fact]
    public void DiscoveryDescriptor_PreservesAdvertisedResponsesTransportsPrivately()
    {
        var info = new CopilotModelInfo
        {
            Id = "gpt-5.6-sol",
            Capabilities = new CopilotModelCapabilities { Family = "gpt-5.6-sol" },
            SupportedEndpoints = ["/responses", "ws:/responses"]
        };

        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        model.ShouldNotBeNull();
        model!.GetType().GetProperties().Select(p => p.Name)
            .ShouldNotContain(name => name.Contains("Transport", StringComparison.OrdinalIgnoreCase));
        CopilotResolvedModelDescriptors.Get(model).SupportsResponsesWebSocket.ShouldBeTrue();
        CopilotResponsesTransportPolicy.Select(model, CopilotResponsesTransportPreference.Auto)
            .ShouldBe(CopilotResponsesWireTransport.WebSocket);
    }

    [Fact]
    public void Auto_UsesSse_WhenWebSocketWasNotAdvertised()
    {
        var model = MapModel(["/responses"]);

        CopilotResponsesTransportPolicy.Select(model, CopilotResponsesTransportPreference.Auto)
            .ShouldBe(CopilotResponsesWireTransport.Sse);
    }

    [Fact]
    public async Task WebSocketAndSseFixtures_ProduceEquivalentNormalizedEvents()
    {
        var events = FixtureEvents();
        var websocket = await ParseJsonEventsAsync(events);
        var sse = await ParseSseAsync(events);

        Project(websocket).ShouldBe(Project(sse));
        websocket.OfType<ThinkingDeltaEvent>().ShouldHaveSingleItem();
        websocket.OfType<TextDeltaEvent>().Count().ShouldBe(2);
        websocket.OfType<ToolCallDeltaEvent>().ShouldHaveSingleItem();
        websocket.OfType<DoneEvent>().Single().Message.Usage.TotalTokens.ShouldBe(15);
    }

    [Fact]
    public async Task JsonEventParser_PreservesStandaloneNewlineDelta()
    {
        var events = new[]
        {
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"\\n\"}",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}"
        };

        var parsed = await ParseJsonEventsAsync(events);

        parsed.OfType<TextDeltaEvent>().Single().Delta.ShouldBe("\n");
        parsed.OfType<DoneEvent>().Single().Message.Content.OfType<TextContent>().Single().Text.ShouldBe("\n");
    }

    [Fact]
    public async Task Auto_WebSocketSetupFailure_FallsBackToSse()
    {
        var socket = new StubWebSocketTransport(connectFailure: new WebSocketException("upgrade rejected"));
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);
        var model = MapModel(["/responses", "ws:/responses"]);

        var result = await provider.Stream(model, BuildContext(), Options()).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        socket.ConnectCount.ShouldBe(1);
        handler.RequestCount.ShouldBe(1);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello\n");
    }

    [Fact]
    public async Task Auto_WebSocketCleanCloseBeforeSemanticOutput_FallsBackToSse()
    {
        var socket = new StubWebSocketTransport();
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(1);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello\n");
    }

    [Fact]
    public async Task Auto_WebSocketCleanCloseAfterSemanticOutput_DoesNotReplayOverSse()
    {
        var socket = new StubWebSocketTransport(messages:
        [
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}"
        ]);
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(0);
        result.StopReason.ShouldBe(StopReason.Error);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello");
    }

    [Fact]
    public async Task Auto_WebSocketFailureAfterSemanticOutput_DoesNotReplayOverSse()
    {
        var socket = new StubWebSocketTransport(messages:
        [
            "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}"
        ], receiveFailure: new WebSocketException("connection lost"));
        var handler = new RecordingHandler(_ => SseResponse(FixtureEvents()));
        var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, socket);

        var result = await provider.Stream(MapModel(["/responses", "ws:/responses"]), BuildContext(), Options())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(0);
        result.StopReason.ShouldBe(StopReason.Error);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("hello");
    }

    private static CopilotResponsesOptions Options() => new() { ApiKey = "test-token" };

    private static LlmModel MapModel(IReadOnlyList<string> endpoints)
        => CopilotModelDiscoveryProvider.MapToLlmModel(new CopilotModelInfo
        {
            Id = "gpt-5.5",
            Name = "GPT-5.5",
            Capabilities = new CopilotModelCapabilities { Family = "gpt-5.5" },
            SupportedEndpoints = endpoints.ToList()
        }) ?? throw new InvalidOperationException();

    private static Context BuildContext() => new(
        "Be helpful.",
        [new UserMessage(new UserMessageContent("hello"), 1)],
        []);

    private static string[] FixtureEvents() =>
    [
        "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"reason_1\",\"type\":\"reasoning\"}}",
        "{\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"reason_1\",\"delta\":\"think\"}",
        "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"reason_1\",\"type\":\"reasoning\"}}",
        "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
        "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello\"}",
        "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"\\n\"}",
        "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}",
        "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"tool_1\",\"call_id\":\"call_1\",\"name\":\"echo\",\"arguments\":\"\",\"type\":\"function_call\"}}",
        "{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"tool_1\",\"delta\":\"{\\\"value\\\":1}\"}",
        "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"tool_1\",\"arguments\":\"{\\\"value\\\":1}\"}",
        "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"tool_1\",\"call_id\":\"call_1\",\"name\":\"echo\",\"arguments\":\"{\\\"value\\\":1}\",\"type\":\"function_call\"}}",
        "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}"
    ];

    private static async Task<List<AssistantMessageEvent>> ParseJsonEventsAsync(IEnumerable<string> json)
    {
        var queue = new Queue<string>(json);
        return await ParseAsync(ct => ValueTask.FromResult(queue.TryDequeue(out var value)
            ? new ResponsesEvent(JsonDocument.Parse(value).RootElement.GetProperty("type").GetString() ?? "", value)
            : null));
    }

    private static async Task<List<AssistantMessageEvent>> ParseSseAsync(IEnumerable<string> json)
    {
        var payload = SsePayload(json);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);
        var llm = new LlmStream();
        await ResponsesStreamParser.ParseAsync(llm, reader, BaseModel(), null, "test", NullLogger.Instance,
            static (_, _, _, _) => { }, null, null, null, CancellationToken.None);
        return await CollectAsync(llm);
    }

    private static async Task<List<AssistantMessageEvent>> ParseAsync(
        Func<CancellationToken, ValueTask<ResponsesEvent?>> read)
    {
        var llm = new LlmStream();
        await ResponsesStreamParser.ParseEventsAsync(llm, read, BaseModel(), null, "test", NullLogger.Instance,
            static (_, _, _, _) => { }, null, null, null, CancellationToken.None);
        return await CollectAsync(llm);
    }

    private static async Task<List<AssistantMessageEvent>> CollectAsync(LlmStream stream)
    {
        var result = new List<AssistantMessageEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private static string[] Project(IEnumerable<AssistantMessageEvent> events) => events.Select(e => e switch
    {
        TextDeltaEvent x => $"text:{x.Delta}",
        ThinkingDeltaEvent x => $"thinking:{x.Delta}",
        ToolCallDeltaEvent x => $"tool:{x.Delta}",
        DoneEvent x => $"done:{x.Message.Usage.TotalTokens}",
        _ => e.Type
    }).ToArray();

    private static LlmModel BaseModel() => new("gpt-5.5", "GPT-5.5", "test", "test", "https://example.test", true,
        ["text"], new ModelCost(0, 0, 0, 0), 1000, 1000);

    private static string SsePayload(IEnumerable<string> events) => string.Join("", events.Select(value =>
    {
        var type = JsonDocument.Parse(value).RootElement.GetProperty("type").GetString();
        return $"event: {type}\ndata: {value}\n\n";
    }));

    private static HttpResponseMessage SseResponse(IEnumerable<string> events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(SsePayload(events), Encoding.UTF8, "text/event-stream")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class StubWebSocketTransport(
        IReadOnlyList<string>? messages = null,
        Exception? connectFailure = null,
        Exception? receiveFailure = null) : ICopilotResponsesWebSocketTransport
    {
        private readonly Queue<string> _messages = new(messages ?? []);
        public int ConnectCount { get; private set; }
        public ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            ConnectCount++;
            return connectFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(connectFailure);
        }
        public ValueTask SendAsync(string payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_messages.TryDequeue(out var message)) return ValueTask.FromResult<string?>(message);
            return receiveFailure is null ? ValueTask.FromResult<string?>(null) : ValueTask.FromException<string?>(receiveFailure);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
