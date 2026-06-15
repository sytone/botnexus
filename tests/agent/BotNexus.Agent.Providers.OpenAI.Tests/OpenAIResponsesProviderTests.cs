using BotNexus.Agent.Providers.OpenAI;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.OpenAI.Tests;

public class OpenAIResponsesProviderTests
{
    [Fact]
    public void Provider_HasCorrectApiValue()
    {
        var provider = new OpenAIResponsesProvider(
            new HttpClient(), NullLogger<OpenAIResponsesProvider>.Instance);

        provider.Api.ShouldBe("openai-responses");
    }

    [Fact]
    public void StreamSimple_WithNullOptions_DoesNotThrow()
    {
        var provider = new OpenAIResponsesProvider(
            new HttpClient(), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5", api: "openai-responses", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context);

        stream.ShouldNotBeNull();
    }

    [Fact]
    public async Task Stream_WhenReasoningConfigured_AddsReasoningInclude()
    {
        var handler = new RecordingHandler();
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5.4", api: "openai-responses", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new OpenAIResponsesOptions
        {
            ApiKey = "test-key",
            ReasoningEffort = "high"
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().ShouldBe("high");
        body.RootElement.GetProperty("include")[0].GetString().ShouldBe("reasoning.encrypted_content");
    }

    [Fact]
    public async Task Stream_WhenNoReasoningOnNonCopilot_SendsReasoningNone()
    {
        var handler = new RecordingHandler();
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5.4", api: "openai-responses", provider: "openai", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new OpenAIResponsesOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().ShouldBe("none");
    }

    [Fact]
    public async Task Stream_WhenLongCacheOnApiOpenAi_SetsPromptCacheRetention()
    {
        var handler = new RecordingHandler();
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5.4", api: "openai-responses", provider: "openai", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new OpenAIResponsesOptions
        {
            ApiKey = "test-key",
            CacheRetention = BotNexus.Agent.Providers.Core.Models.CacheRetention.Long,
            SessionId = "session-123"
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("prompt_cache_retention").GetString().ShouldBe("24h");
    }

    [Fact]
    public async Task StreamSimple_ClampsExtraHigh_WhenModelDoesNotSupportXhigh()
    {
        var handler = new RecordingHandler();
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5.1", api: "openai-responses", provider: "openai", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context, new BotNexus.Agent.Providers.Core.SimpleStreamOptions
        {
            ApiKey = "test-key",
            Reasoning = BotNexus.Agent.Providers.Core.Models.ThinkingLevel.ExtraHigh
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().ShouldBe("high");
    }

    [Fact]
    public async Task Stream_SystemPromptUsesRoleContentShorthand()
    {
        var handler = new RecordingHandler();
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5.4", api: "openai-responses", reasoning: true);
        var context = TestHelpers.MakeContext(systemPrompt: "sys prompt");

        var stream = provider.Stream(model, context, new OpenAIResponsesOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var firstInput = body.RootElement.GetProperty("input")[0];
        firstInput.GetProperty("role").GetString().ShouldBe("developer");
        firstInput.GetProperty("content").GetString().ShouldBe("sys prompt");
        firstInput.TryGetProperty("type", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Stream_DoesNotSendPreviousResponseId()
    {
        var handler = new RecordingHandler();
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var model = TestHelpers.MakeModel(id: "gpt-5.4", api: "openai-responses", reasoning: true);
        var context = new BotNexus.Agent.Providers.Core.Models.Context(
            "system",
            [
                new BotNexus.Agent.Providers.Core.Models.UserMessage(new BotNexus.Agent.Providers.Core.Models.UserMessageContent("hello"), timestamp),
                new BotNexus.Agent.Providers.Core.Models.AssistantMessage(
                    Content: [new BotNexus.Agent.Providers.Core.Models.TextContent("hi")],
                    Api: model.Api,
                    Provider: model.Provider,
                    ModelId: model.Id,
                    Usage: BotNexus.Agent.Providers.Core.Models.Usage.Empty(),
                    StopReason: BotNexus.Agent.Providers.Core.Models.StopReason.Stop,
                    ErrorMessage: null,
                    ResponseId: "resp_prev",
                    Timestamp: timestamp)
            ]);

        var stream = provider.Stream(model, context, new OpenAIResponsesOptions
        {
            ApiKey = "test-key",
            PreviousResponseId = "explicit_prev"
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.TryGetProperty("previous_response_id", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Stream_CopilotHeadersCanBeOverriddenByOptionsHeaders()
    {
        var handler = new RecordingHandler();
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(
            id: "gpt-4o",
            api: "openai-responses",
            provider: "github-copilot",
            reasoning: true);
        var context = TestHelpers.MakeContext();
        var stream = provider.Stream(model, context, new OpenAIResponsesOptions
        {
            ApiKey = "test-key",
            Headers = new Dictionary<string, string>
            {
                ["Openai-Intent"] = "custom-intent"
            }
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.LastRequestHeaders.ShouldContainKey("Openai-Intent");
        handler.LastRequestHeaders["Openai-Intent"].ShouldContain("custom-intent");
    }

    [Fact]
    public async Task Stream_ParsesResponsesSse_AssemblesTextAndToolCall()
    {
        // Drives a multi-event Responses SSE stream end-to-end through the provider, which now flows
        // through the extracted OpenAIResponsesStreamParser (#1404). Asserts the parser assembles
        // streamed text + a function call into the final message and emits the expected event types.
        // The OpenAI Responses API frames each event with an `event:` line whose value is the event
        // type, followed by a `data:` JSON payload -- the parser dispatches on that `event:` value
        // (the JSON `type` field is informational). The fixture below mirrors that wire shape, the
        // same way CopilotResponsesProviderParityTests does.
        var sse = string.Join("\n\n", new[]
        {
            "event: response.created\n" +
            """data: {"type":"response.created","response":{"id":"resp_42"}}""",
            "event: response.output_item.added\n" +
            """data: {"type":"response.output_item.added","item":{"type":"message","id":"msg_1"}}""",
            "event: response.output_text.delta\n" +
            """data: {"type":"response.output_text.delta","item_id":"msg_1","delta":"Hello"}""",
            "event: response.output_text.delta\n" +
            """data: {"type":"response.output_text.delta","item_id":"msg_1","delta":" world"}""",
            "event: response.output_item.done\n" +
            """data: {"type":"response.output_item.done","item":{"type":"message","id":"msg_1","phase":"final_answer"}}""",
            "event: response.output_item.added\n" +
            """data: {"type":"response.output_item.added","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":""}}""",
            "event: response.function_call_arguments.delta\n" +
            """data: {"type":"response.function_call_arguments.delta","call_id":"call_1","delta":"{\"city\":\"Paris\"}"}""",
            "event: response.function_call_arguments.done\n" +
            """data: {"type":"response.function_call_arguments.done","call_id":"call_1","arguments":"{\"city\":\"Paris\"}"}""",
            "event: response.output_item.done\n" +
            """data: {"type":"response.output_item.done","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}""",
            "event: response.completed\n" +
            """data: {"type":"response.completed","response":{"id":"resp_42","status":"completed","usage":{"input_tokens":5,"output_tokens":7,"total_tokens":12}}}""",
            "data: [DONE]"
        });

        var handler = new FixedSseHandler(sse);
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);
        var model = TestHelpers.MakeModel(id: "gpt-5.4", api: "openai-responses", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new OpenAIResponsesOptions { ApiKey = "test-key" });

        var eventTypes = new List<string>();
        await foreach (var evt in stream.WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
            eventTypes.Add(evt.Type);

        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        eventTypes.ShouldContain("start");
        eventTypes.ShouldContain("text_delta");
        eventTypes.ShouldContain("toolcall_end");
        eventTypes.ShouldContain("done");

        var text = result.Content.OfType<BotNexus.Agent.Providers.Core.Models.TextContent>().Single();
        text.Text.ShouldBe("Hello world");

        var toolCall = result.Content.OfType<BotNexus.Agent.Providers.Core.Models.ToolCallContent>().Single();
        toolCall.Name.ShouldBe("get_weather");
        toolCall.Arguments["city"]!.ToString().ShouldBe("Paris");

        result.StopReason.ShouldBe(BotNexus.Agent.Providers.Core.Models.StopReason.ToolUse);
        result.ResponseId.ShouldBe("resp_42");
        result.Usage.Output.ShouldBe(7);
    }

    [Fact]
    public async Task Stream_ParsesResponsesSseError_EmitsErrorMessage()
    {
        // The error event path also lives in the extracted parser (via the emitError callback).
        var sse = string.Join("\n", new[]
        {
            "event: error",
            """data: {"message":"upstream exploded"}"""
        });

        var handler = new FixedSseHandler(sse);
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);
        var model = TestHelpers.MakeModel(id: "gpt-5.4", api: "openai-responses", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new OpenAIResponsesOptions { ApiKey = "test-key" });
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldBe(BotNexus.Agent.Providers.Core.Models.StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
    }

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public Dictionary<string, string> LastRequestHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequestHeaders = request.Headers.ToDictionary(
                h => h.Key,
                h => string.Join(",", h.Value),
                StringComparer.OrdinalIgnoreCase);
            return SseResponse("""
                data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}
                data: [DONE]
                """);
        }
    }

    private sealed class FixedSseHandler(string ssePayload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(SseResponse(ssePayload));
    }
}
