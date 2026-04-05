using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Providers.Anthropic;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Anthropic.Tests;

public class AnthropicProviderAlignmentTests
{
    [Fact]
    public async Task Stream_UsesModelMaxTokensDividedByThree_WhenOptionsMaxTokensNotProvided()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var httpClient = new HttpClient(handler);
        var provider = new AnthropicProvider(httpClient);
        var model = TestHelpers.MakeModel(maxTokens: 12000);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        handler.RequestCount.Should().Be(1);
        handler.RequestBody.Should().NotBeNull();

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(model.MaxTokens / 3);
    }

    [Theory]
    [InlineData("refusal", StopReason.Refusal)]
    [InlineData("pause_turn", StopReason.PauseTurn)]
    [InlineData("sensitive", StopReason.Sensitive)]
    public async Task Stream_MapsAnthropicStopReasons(string anthropicReason, StopReason expected)
    {
        var payload = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"__STOP_REASON__"}}

            event: message_stop
            data: {"type":"message_stop"}
            """.Replace("__STOP_REASON__", anthropicReason, StringComparison.Ordinal);
        var handler = new RecordingHandler(_ => SseResponse(payload));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        result.StopReason.Should().Be(expected);
    }

    [Fact]
    public async Task Stream_TracksCacheReadAndWriteTokensInUsage()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":11}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5,"cache_read_input_tokens":7,"cache_creation_input_tokens":13}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        result.Usage.Input.Should().Be(11);
        result.Usage.Output.Should().Be(5);
        result.Usage.CacheRead.Should().Be(7);
        result.Usage.CacheWrite.Should().Be(13);
        result.Usage.TotalTokens.Should().Be(36);
    }

    [Fact]
    public async Task Stream_UsesInjectedHttpClient()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        handler.RequestCount.Should().Be(1);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.AbsoluteUri.Should().Be($"{model.BaseUrl}/v1/messages");
    }

    [Fact]
    public void Constructor_ThrowsWhenHttpClientIsNull()
    {
        var act = () => _ = new AnthropicProvider(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("httpClient");
    }

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? RequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return responseFactory(request);
        }
    }
}
