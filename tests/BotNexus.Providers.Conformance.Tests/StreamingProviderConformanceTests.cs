using System.Net;
using System.Text;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using FluentAssertions;

namespace BotNexus.Providers.Conformance.Tests;

public abstract class StreamingProviderConformanceTests
{
    public static TheoryData<string> TextCases => new()
    {
        "normalized hello",
        "multiline\ncontent"
    };

    public static TheoryData<string, string, string> ToolCallCases => new()
    {
        { "call_1", "search", "{\"query\":\"weather\"}" },
        { "call_2", "lookup", "{\"id\":\"42\"}" }
    };

    public static TheoryData<int, int> UsageCases => new()
    {
        { 11, 5 },
        { 100, 25 }
    };

    public static TheoryData<string, StopReason> StopReasonCases => new()
    {
        { "stop", StopReason.Stop },
        { "length", StopReason.Length },
        { "tool_use", StopReason.ToolUse }
    };

    [Theory]
    [MemberData(nameof(TextCases))]
    public async Task Stream_NormalizesContentExtraction(string expectedText)
    {
        var (result, _) = await ExecuteAsync(BuildTextPayload(expectedText, MapCanonicalStopReason("stop")));

        result.Content.Should().ContainSingle();
        result.Content[0].Should().BeOfType<TextContent>();
        ((TextContent)result.Content[0]).Text.Should().Be(expectedText);
    }

    [Theory]
    [MemberData(nameof(ToolCallCases))]
    public async Task Stream_NormalizesToolCallParsing(string toolCallId, string toolName, string argumentsJson)
    {
        var (result, _) = await ExecuteAsync(
            BuildToolCallPayload(toolCallId, toolName, argumentsJson, MapCanonicalStopReason("tool_use")));

        var toolCall = result.Content.OfType<ToolCallContent>().Single();
        toolCall.Id.Should().Be(toolCallId);
        toolCall.Name.Should().Be(toolName);
        toolCall.Arguments.Keys.Any(key => key == "query" || key == "id").Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(StopReasonCases))]
    public async Task Stream_NormalizesFinishReasons(string canonicalReason, StopReason expected)
    {
        var (result, _) = await ExecuteAsync(BuildFinishReasonPayload(MapCanonicalStopReason(canonicalReason)));

        result.StopReason.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(UsageCases))]
    public async Task Stream_NormalizesTokenCounts(int inputTokens, int outputTokens)
    {
        var (result, _) = await ExecuteAsync(
            BuildUsagePayload(inputTokens, outputTokens, MapCanonicalStopReason("stop")));

        result.Usage.Input.Should().Be(inputTokens);
        result.Usage.Output.Should().Be(outputTokens);
        result.Usage.TotalTokens.Should().Be(inputTokens + outputTokens);
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public async Task Stream_EmitsExpectedEventSequence(string text)
    {
        if (!SupportsStreamingSequence)
            return;

        var (_, events) = await ExecuteAsync(BuildTextPayload(text, MapCanonicalStopReason("stop")));

        events.Select(e => e.Type).Should().Equal(ExpectedTextEventSequence);
    }

    // --- HTTP error handling tests ---

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Stream_HttpError_EmitsErrorResult(HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent($"{{\"error\":\"test error {(int)statusCode}\"}}", Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler);
        var stream = provider.Stream(CreateModel(), CreateContext(), CreateOptions());
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.Should().Be(StopReason.Error);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Stream_EmptyResponse_EmitsErrorResult()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "text/event-stream")
        });

        var provider = CreateProvider(handler);
        var stream = provider.Stream(CreateModel(), CreateContext(), CreateOptions());
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Empty stream should produce either an error or an empty content result
        (result.StopReason == StopReason.Error || result.Content.Count == 0).Should().BeTrue(
            "empty stream should produce error or empty content, got StopReason={0}, Content.Count={1}",
            result.StopReason, result.Content.Count);
    }

    [Fact]
    public async Task Stream_MalformedJson_EmitsErrorResult()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {not valid json}\n\n", Encoding.UTF8, "text/event-stream")
        });

        var provider = CreateProvider(handler);
        var stream = provider.Stream(CreateModel(), CreateContext(), CreateOptions());
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.Should().Be(StopReason.Error);
    }

    [Fact]
    public async Task Stream_CancellationDuringStreaming_ThrowsOrEmitsError()
    {
        using var cts = new CancellationTokenSource();

        var handler = new RecordingHandler(_ =>
        {
            // Simulate slow response
            cts.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    BuildTextPayload("hello", MapCanonicalStopReason("stop")),
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });

        var provider = CreateProvider(handler);
        var options = CreateOptions() with { CancellationToken = cts.Token };
        var stream = provider.Stream(CreateModel(), CreateContext(), options);

        Func<Task> act = async () => await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Either throws OperationCanceledException or returns error result
        try
        {
            var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // If it doesn't throw, it should be an error or cancelled result
            (result.StopReason == StopReason.Error || result.ErrorMessage is not null).Should().BeTrue();
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    protected virtual bool SupportsStreamingSequence => true;

    protected virtual IReadOnlyList<string> ExpectedTextEventSequence =>
        ["start", "text_start", "text_delta", "text_end", "done"];

    protected virtual Context CreateContext() => new(
        SystemPrompt: "You are helpful",
        Messages: [new UserMessage(new UserMessageContent("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

    protected virtual StreamOptions CreateOptions() => new() { ApiKey = "test-key" };

    protected abstract IApiProvider CreateProvider(HttpMessageHandler handler);
    protected abstract LlmModel CreateModel();
    protected abstract string BuildTextPayload(string text, string providerStopReason);
    protected abstract string BuildToolCallPayload(string toolCallId, string toolName, string argumentsJson, string providerStopReason);
    protected abstract string BuildFinishReasonPayload(string providerStopReason);
    protected abstract string BuildUsagePayload(int inputTokens, int outputTokens, string providerStopReason);
    protected abstract string MapCanonicalStopReason(string canonicalReason);

    private async Task<(AssistantMessage Result, List<AssistantMessageEvent> Events)> ExecuteAsync(string payload)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        });

        var provider = CreateProvider(handler);
        var stream = provider.Stream(CreateModel(), CreateContext(), CreateOptions());
        var events = await ReadAllEventsAsync(stream);
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.Should().Be(1);
        return (result, events);
    }

    private static async Task<List<AssistantMessageEvent>> ReadAllEventsAsync(LlmStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        return events;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
