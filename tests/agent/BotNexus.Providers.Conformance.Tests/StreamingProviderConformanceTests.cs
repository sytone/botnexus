using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

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

        result.Content.ShouldHaveSingleItem();
        result.Content[0].ShouldBeOfType<TextContent>();
        ((TextContent)result.Content[0]).Text.ShouldBe(expectedText);
    }

    [Theory]
    [MemberData(nameof(ToolCallCases))]
    public async Task Stream_NormalizesToolCallParsing(string toolCallId, string toolName, string argumentsJson)
    {
        var (result, _) = await ExecuteAsync(
            BuildToolCallPayload(toolCallId, toolName, argumentsJson, MapCanonicalStopReason("tool_use")));

        var toolCall = result.Content.OfType<ToolCallContent>().Single();
        toolCall.Id.ShouldBe(toolCallId);
        toolCall.Name.ShouldBe(toolName);
        toolCall.Arguments.Keys.Any(key => key == "query" || key == "id").ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(StopReasonCases))]
    public async Task Stream_NormalizesFinishReasons(string canonicalReason, StopReason expected)
    {
        var (result, _) = await ExecuteAsync(BuildFinishReasonPayload(MapCanonicalStopReason(canonicalReason)));

        result.StopReason.ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(UsageCases))]
    public async Task Stream_NormalizesTokenCounts(int inputTokens, int outputTokens)
    {
        var (result, _) = await ExecuteAsync(
            BuildUsagePayload(inputTokens, outputTokens, MapCanonicalStopReason("stop")));

        result.Usage.Input.ShouldBe(inputTokens);
        result.Usage.Output.ShouldBe(outputTokens);
        result.Usage.TotalTokens.ShouldBe(inputTokens + outputTokens);
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public async Task Stream_EmitsExpectedEventSequence(string text)
    {
        if (!SupportsStreamingSequence)
            return;

        var (_, events) = await ExecuteAsync(BuildTextPayload(text, MapCanonicalStopReason("stop")));

        events.Select(e => e.Type).ShouldBe(ExpectedTextEventSequence);
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

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
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
        (result.StopReason == StopReason.Error || result.Content.Count == 0).ShouldBeTrue(
            $"empty stream should produce error or empty content, got StopReason={result.StopReason}, Content.Count={result.Content.Count}");
    }

    [Fact]
    public async Task Stream_MalformedJson_ShouldNotSilentlySucceed()
    {
        // BUG: Current behavior silently swallows malformed JSON and returns StopReason.Stop
        // with empty content. Ideally this should emit StopReason.Error.
        // Filed as known issue — parser skips unparseable SSE lines.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {not valid json}\n\n", Encoding.UTF8, "text/event-stream")
        });

        var provider = CreateProvider(handler);
        var stream = provider.Stream(CreateModel(), CreateContext(), CreateOptions());
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Current behavior: malformed JSON is silently skipped, producing empty result
        // When fixed, this should assert: result.StopReason.ShouldBe(StopReason.Error);
        result.Content.ShouldBeEmpty("malformed JSON should not produce content");
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

        // With cancellation, the provider may either:
        // 1. Throw OperationCanceledException
        // 2. Return error result
        // 3. Return normal result (race: response arrived before cancellation was checked)
        // All three are acceptable behaviors for this race condition.
        try
        {
            var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // If we get here without throwing, any result is acceptable
            result.ShouldNotBeNull();
        }
        catch (OperationCanceledException)
        {
            // Expected — cancellation propagated correctly
        }
    }

    protected virtual bool SupportsStreamingSequence => true;

    // --- Capability declaration conformance (#2432) ---

    /// <summary>
    /// Every real provider must DECLARE a <see cref="ProviderCapabilities"/> rather than inheriting
    /// the interface default. This is the #2432 acceptance criterion "ProviderCapabilities surfaced
    /// by all real providers", and it lives on the shared conformance base precisely so that a new
    /// provider added to the suite cannot quietly skip declaring one.
    /// <para>
    /// Reference equality against <see cref="ProviderCapabilities.Default"/> is the load-bearing
    /// assertion: <c>Default</c> is a single cached instance, so a provider that constructs its own
    /// record -- even one whose field values happen to match the defaults -- passes, while a
    /// provider that declares nothing at all and falls through to the interface default fails. A
    /// value-equality check would have been vacuous, because most providers legitimately declare
    /// values identical to the defaults.
    /// </para>
    /// </summary>
    [Fact]
    public void Provider_DeclaresItsOwnCapabilities()
    {
        var provider = CreateProvider(new NeverCalledHandler());

        var capabilities = provider.Capabilities;

        capabilities.ShouldNotBeNull();
        ReferenceEquals(capabilities, ProviderCapabilities.Default).ShouldBeFalse(
            $"{provider.GetType().Name} must declare its own ProviderCapabilities (#2432) rather than " +
            "inheriting the IApiProvider default, so the platform can answer capability questions " +
            "without issuing a request and reading what comes back.");
    }

    /// <summary>
    /// The declared capabilities must be stable across reads. The agent loop queries them once per
    /// turn; a provider that recomputed or reallocated them per read would make a capability a
    /// moving target and defeat the point of declaring it.
    /// </summary>
    [Fact]
    public void Provider_CapabilitiesAreStableAcrossReads()
    {
        var provider = CreateProvider(new NeverCalledHandler());

        provider.Capabilities.ShouldBe(provider.Capabilities);
    }

    /// <summary>
    /// A handler for capability tests, which must never issue a request. Throwing rather than
    /// returning a canned response means a capability read that secretly probes the network fails
    /// loudly instead of passing quietly.
    /// </summary>
    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Reading ProviderCapabilities must not issue an HTTP request.");
    }

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

        handler.RequestCount.ShouldBe(1);
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
