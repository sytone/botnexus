using BotNexus.Agent.Providers.OpenAI;
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
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

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
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

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
            CacheRetention = BotNexus.Agent.Providers.Core.Models.CacheRetention.Long,
            SessionId = "session-123"
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

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

        var stream = provider.StreamSimple(model, context, new BotNexus.Agent.Providers.Core.SimpleStreamOptions
        {
            ApiKey = "test-key",
            Reasoning = BotNexus.Agent.Providers.Core.Models.ThinkingLevel.ExtraHigh
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("high");
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
        firstInput.GetProperty("role").GetString().Should().Be("developer");
        firstInput.GetProperty("content").GetString().Should().Be("sys prompt");
        firstInput.TryGetProperty("type", out _).Should().BeFalse();
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
        body.RootElement.TryGetProperty("previous_response_id", out _).Should().BeFalse();
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

        handler.LastRequestHeaders.Should().ContainKey("Openai-Intent");
        handler.LastRequestHeaders["Openai-Intent"].Should().Contain("custom-intent");
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
}
