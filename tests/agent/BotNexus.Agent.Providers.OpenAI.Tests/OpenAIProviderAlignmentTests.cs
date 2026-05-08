using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.OpenAI.Tests;

public class OpenAIProviderAlignmentTests
{
    [Fact]
    public async Task Stream_UsesInjectedHttpClient()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            data: {"id":"resp_1","choices":[{"delta":{"content":"hello"}}]}
            data: {"choices":[{"finish_reason":"stop","delta":{}}]}
            data: [DONE]
            """));
        var provider = new OpenAICompletionsProvider(
            new HttpClient(handler),
            NullLogger<OpenAICompletionsProvider>.Instance);
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(1);
        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.AbsoluteUri.ShouldBe($"{model.BaseUrl}/chat/completions");
    }

    [Fact]
    public async Task Stream_ParsesReasoningDeltas_FromAllSupportedFields()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            data: {"id":"resp_1","choices":[{"delta":{"reasoning_content":"step-1 "}}]}
            data: {"choices":[{"delta":{"reasoning":"step-2 "}}]}
            data: {"choices":[{"delta":{"reasoning_text":"step-3"}}]}
            data: {"choices":[{"delta":{"content":"final answer"},"finish_reason":"stop"}]}
            data: [DONE]
            """));
        var provider = new OpenAICompletionsProvider(
            new HttpClient(handler),
            NullLogger<OpenAICompletionsProvider>.Instance);
        var model = TestHelpers.MakeModel(reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var events = await ReadAllEventsAsync(stream);
        var final = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        events.Where(e => e is ThinkingStartEvent).ShouldHaveSingleItem();
        events.OfType<ThinkingDeltaEvent>().Select(e => e.Delta)
            .ShouldBe(new[] { "step-1 ", "step-2 ", "step-3" });
        events.Where(e => e is ThinkingEndEvent).ShouldHaveSingleItem();

        final.Content.Count().ShouldBe(2);
        final.Content[0].ShouldBeOfType<ThinkingContent>();
        final.Content[1].ShouldBeOfType<TextContent>();
        ((ThinkingContent)final.Content[0]).Thinking.ShouldBe("step-1 step-2 step-3");
        ((TextContent)final.Content[1]).Text.ShouldBe("final answer");
    }

    [Fact]
    public async Task Stream_CapturesThinkingSignatureFromReasoningField()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            data: {"id":"resp_1","choices":[{"delta":{"reasoning_text":"step-1"}}]}
            data: {"choices":[{"delta":{"content":"done"},"finish_reason":"stop"}]}
            data: [DONE]
            """));
        var provider = new OpenAICompletionsProvider(
            new HttpClient(handler),
            NullLogger<OpenAICompletionsProvider>.Instance);
        var model = TestHelpers.MakeModel(reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var final = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var thinking = final.Content.OfType<ThinkingContent>().Single();
        thinking.Thinking.ShouldBe("step-1");
        thinking.ThinkingSignature.ShouldBe("reasoning_text");
    }

    [Fact]
    public async Task Stream_MatchesReasoningDetailsByToolCallId()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            data: {"id":"resp_1","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"a","arguments":"{}"}},{"index":1,"id":"call_b","type":"function","function":{"name":"b","arguments":"{}"}}],"reasoning_details":[{"type":"reasoning.encrypted","id":"call_b","data":"sig-b"},{"type":"reasoning.encrypted","id":"call_a","data":"sig-a"}]}}]}
            data: {"choices":[{"finish_reason":"tool_calls","delta":{}}]}
            data: [DONE]
            """));
        var provider = new OpenAICompletionsProvider(
            new HttpClient(handler),
            NullLogger<OpenAICompletionsProvider>.Instance);
        var model = TestHelpers.MakeModel(reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var final = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var toolCalls = final.Content.OfType<ToolCallContent>().ToDictionary(tc => tc.Id, tc => tc.ThoughtSignature);
        toolCalls["call_a"]!.ShouldContain("\"data\":\"sig-a\"");
        toolCalls["call_b"]!.ShouldContain("\"data\":\"sig-b\"");
    }

    [Fact]
    public void Constructor_ThrowsWhenHttpClientIsNull()
    {
        var act = () => _ = new OpenAICompletionsProvider(
            null!,
            NullLogger<OpenAICompletionsProvider>.Instance);

        act.ShouldThrow<ArgumentNullException>()
            .ParamName.ShouldBe("httpClient");
    }

    private static async Task<List<AssistantMessageEvent>> ReadAllEventsAsync(LlmStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(responseFactory(request));
        }
    }
}
