using BotNexus.Providers.OpenAI;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Providers.OpenAI.Tests;

public class OpenAIResponsesProviderTests
{
    [Fact]
    public void Provider_HasCorrectApiValue()
    {
        var provider = new OpenAIResponsesProvider(
            new HttpClient(), NullLogger<OpenAIResponsesProvider>.Instance);

        provider.Api.Should().Be("openai-responses");
    }

    [Fact]
    public void StreamSimple_WithNullOptions_DoesNotThrow()
    {
        var provider = new OpenAIResponsesProvider(
            new HttpClient(), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5", api: "openai-responses", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context);

        stream.Should().NotBeNull();
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
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("high");
        body.RootElement.GetProperty("include")[0].GetString().Should().Be("reasoning.encrypted_content");
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
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("none");
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
            CacheRetention = BotNexus.Providers.Core.Models.CacheRetention.Long,
            SessionId = "session-123"
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("prompt_cache_retention").GetString().Should().Be("24h");
    }

    [Fact]
    public async Task StreamSimple_ClampsExtraHigh_WhenModelDoesNotSupportXhigh()
    {
        var handler = new RecordingHandler();
        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5.1", api: "openai-responses", provider: "openai", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context, new BotNexus.Providers.Core.SimpleStreamOptions
        {
            ApiKey = "test-key",
            Reasoning = BotNexus.Providers.Core.Models.ThinkingLevel.ExtraHigh
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("high");
    }

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return SseResponse("""
                data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}
                data: [DONE]
                """);
        }
    }
}
