using System.Net;
using System.Text;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Streaming;
using BotNexus.Providers.OpenAI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Providers.OpenAI.Tests;

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
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        handler.RequestCount.Should().Be(1);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.AbsoluteUri.Should().Be($"{model.BaseUrl}/chat/completions");
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
        var final = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        events.Should().ContainSingle(e => e is ThinkingStartEvent);
        events.OfType<ThinkingDeltaEvent>().Select(e => e.Delta)
            .Should().Equal("step-1 ", "step-2 ", "step-3");
        events.Should().ContainSingle(e => e is ThinkingEndEvent);

        final.Content.Should().HaveCount(2);
        final.Content[0].Should().BeOfType<ThinkingContent>();
        final.Content[1].Should().BeOfType<TextContent>();
        ((ThinkingContent)final.Content[0]).Thinking.Should().Be("step-1 step-2 step-3");
        ((TextContent)final.Content[1]).Text.Should().Be("final answer");
    }

    [Fact]
    public void Constructor_ThrowsWhenHttpClientIsNull()
    {
        var act = () => _ = new OpenAICompletionsProvider(
            null!,
            NullLogger<OpenAICompletionsProvider>.Instance);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("httpClient");
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
