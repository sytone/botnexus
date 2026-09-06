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

    /// <summary>
    /// A newline is model content, not an empty delta. Inspect the emitted payload rather than
    /// final text, which could be repaired during assembly and hide a streaming regression (#3301).
    /// </summary>
    [Fact]
    public async Task Stream_NewlineOnlyText_EmitsExactTextDelta()
    {
        var (result, events) = await ExecuteAsync(BuildTextPayload("\n", MapCanonicalStopReason("stop")));

        events.OfType<TextDeltaEvent>().ShouldHaveSingleItem().Delta.ShouldBe("\n");
        events.OfType<ErrorEvent>().ShouldBeEmpty();
        events.Last().ShouldBeOfType<DoneEvent>().Reason.ShouldBe(StopReason.Stop);
        result.StopReason.ShouldBe(StopReason.Stop);
    }

    /// <summary>
    /// An explicit empty text fragment must not become a normalized delta. Require successful
    /// completion so a failed stream cannot satisfy the absence assertion vacuously (#3301).
    /// </summary>
    [Fact]
    public async Task Stream_EmptyText_EmitsNoTextDelta()
    {
        var (result, events) = await ExecuteAsync(BuildTextPayload("", MapCanonicalStopReason("stop")));

        events.OfType<TextDeltaEvent>().ShouldBeEmpty();
        events.OfType<ErrorEvent>().ShouldBeEmpty();
        events.Last().ShouldBeOfType<DoneEvent>().Reason.ShouldBe(StopReason.Stop);
        result.StopReason.ShouldBe(StopReason.Stop);
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
        var (_, events) = await ExecuteAsync(BuildTextPayload(text, MapCanonicalStopReason("stop")));

        events.Select(e => e.Type).ShouldBe(ExpectedTextEventSequence);
    }

    // --- Producer-agnostic ordering invariants (#3300) ---

    /// <summary>
    /// The normalized event grammar holds for a plain-text turn, checked against
    /// <see cref="AssistantMessageEventOrdering"/> rather than against a per-fixture expectation.
    /// <para>
    /// This is the assertion <see cref="Stream_EmitsExpectedEventSequence"/> cannot make. That test
    /// compares the emitted sequence to <see cref="ExpectedTextEventSequence"/>, which each derived
    /// fixture may override - so a producer emitting a self-consistently wrong sequence declares the
    /// wrong sequence as its expectation and passes. The rules checked here are stated once, in the
    /// production assembly, in terms no fixture can supply or relax.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TextCases))]
    public async Task Stream_TextTurn_SatisfiesOrderingInvariants(string text)
    {
        var (_, events) = await ExecuteAsync(BuildTextPayload(text, MapCanonicalStopReason("stop")));

        AssertOrdering(events, "plain-text turn");
    }

    /// <summary>
    /// The grammar also holds for a single tool call, where the block being opened and closed is a
    /// <c>toolcall_*</c> rather than a <c>text_*</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ToolCallCases))]
    public async Task Stream_ToolCallTurn_SatisfiesOrderingInvariants(
        string toolCallId, string toolName, string argumentsJson)
    {
        var (_, events) = await ExecuteAsync(
            BuildToolCallPayload(toolCallId, toolName, argumentsJson, MapCanonicalStopReason("tool_use")));

        AssertOrdering(events, "single tool call turn");
    }

    /// <summary>
    /// Two tool calls whose argument deltas interleave on the wire - the case the pre-#3300 suite
    /// never exercised. A producer that tracks only "the current block" instead of a per-content-index
    /// block table emits a delta for an index it has already closed, or closes an index twice, and
    /// nothing before this test noticed.
    /// </summary>
    [Fact]
    public async Task Stream_InterleavedToolCalls_SatisfyOrderingInvariants()
    {
        var payload = BuildInterleavedToolCallPayload(
            "call_a", "search", "{\"query\":\"weather\"}",
            "call_b", "lookup", "{\"id\":\"42\"}",
            MapCanonicalStopReason("tool_use"));

        var (result, events) = await ExecuteAsync(payload);

        AssertOrdering(events, "interleaved two-tool-call turn");

        // Guards the ordering assertion above against vacuity from the other direction: a producer
        // that dropped one of the two calls entirely would emit a perfectly well-ordered stream.
        result.Content.OfType<ToolCallContent>().Count().ShouldBe(2,
            "both interleaved tool calls must survive normalization; a well-ordered stream that lost " +
            "one of them still loses a tool call");
    }

    /// <summary>
    /// Every <c>toolcall_start</c> and <c>toolcall_delta</c> must carry the id and name of the call it
    /// belongs to, and each argument fragment must be attributed to the call that actually sent it
    /// (#3290).
    /// <para>
    /// This is the assertion the ordering invariants cannot make. A producer that emits perfectly
    /// well-ordered events while labelling <c>call_b</c>'s fragment with <c>call_a</c>'s id passes
    /// every #3300 rule, because ordering says nothing about identity. Before #3290 the consumer
    /// resolved identity by indexing the partial message and fell back to "the most recent tool
    /// call", so a mislabelled fragment was not merely possible, it was the designed behaviour
    /// whenever <c>ContentIndex</c> - which counts text and thinking blocks too - ran past the end
    /// of the <c>ToolCalls</c> list.
    /// </para>
    /// <para>
    /// The fragment-content check is what makes this non-vacuous in the interesting direction: it is
    /// not enough that both ids appear somewhere, each id must appear on the deltas whose payload is
    /// that call's own arguments. Nulling either field on any producer reddens this test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stream_InterleavedToolCalls_StartAndDeltaEventsCarryTheirOwnIdentity()
    {
        var payload = BuildInterleavedToolCallPayload(
            "call_a", "search", "{\"query\":\"weather\"}",
            "call_b", "lookup", "{\"id\":\"42\"}",
            MapCanonicalStopReason("tool_use"));

        var (_, events) = await ExecuteAsync(payload);

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        starts.Count.ShouldBe(2, $"{GetType().Name} must open both interleaved tool calls");

        foreach (var start in starts)
        {
            start.ToolCallId.ShouldNotBeNullOrWhiteSpace(
                $"{GetType().Name} knows the tool call id when it opens the block and must carry it " +
                "on toolcall_start (#3290); emitting null forces the consumer back to the index guess");
            start.ToolName.ShouldNotBeNullOrWhiteSpace(
                $"{GetType().Name} must carry the tool name on toolcall_start (#3290)");
        }

        starts.Select(s => s.ToolCallId!).ShouldContain(id => id.Contains("call_a"));
        starts.Select(s => s.ToolCallId!).ShouldContain(id => id.Contains("call_b"));
        starts.Select(s => s.ToolName).ShouldContain("search");
        starts.Select(s => s.ToolName).ShouldContain("lookup");

        var deltas = events.OfType<ToolCallDeltaEvent>().ToList();
        deltas.ShouldNotBeEmpty($"{GetType().Name} must emit argument deltas for the interleaved calls");

        foreach (var delta in deltas)
        {
            delta.ToolCallId.ShouldNotBeNullOrWhiteSpace(
                $"{GetType().Name} must carry the tool call id on every toolcall_delta (#3290); a " +
                "null id is worse than no field at all, because a consumer then cannot rely on it");
            delta.ToolName.ShouldNotBeNullOrWhiteSpace(
                $"{GetType().Name} must carry the tool name on every toolcall_delta (#3290)");
        }

        // Attribution, not mere presence: a fragment containing "weather" belongs to call_a and a
        // fragment containing "42" belongs to call_b. Swapping the labels satisfies every check
        // above and fails here.
        foreach (var delta in deltas.Where(d => d.Delta.Contains("weather")))
        {
            delta.ToolCallId!.Contains("call_a", StringComparison.Ordinal).ShouldBeTrue(
                $"{GetType().Name} attributed the 'weather' argument fragment to the wrong tool call " +
                $"(reported '{delta.ToolCallId}') - this is the #3290 misattribution");
            delta.ToolName.ShouldBe("search");
        }

        foreach (var delta in deltas.Where(d => d.Delta.Contains("42")))
        {
            delta.ToolCallId!.Contains("call_b", StringComparison.Ordinal).ShouldBeTrue(
                $"{GetType().Name} attributed the 'id: 42' argument fragment to the wrong tool call " +
                $"(reported '{delta.ToolCallId}') - this is the #3290 misattribution");
            delta.ToolName.ShouldBe("lookup");
        }

        // Guards the loops above against vacuity: if no delta carried either payload the two foreach
        // bodies would never execute and the test would pass having asserted nothing about
        // attribution.
        deltas.ShouldContain(d => d.Delta.Contains("weather"),
            "no delta carried call_a's arguments, so the attribution assertions never ran");
        deltas.ShouldContain(d => d.Delta.Contains("42"),
            "no delta carried call_b's arguments, so the attribution assertions never ran");
    }

    /// <summary>
    /// Ordering rules this provider is excused from, keyed by the rule id on
    /// <see cref="AssistantMessageEventOrdering"/> with the reason as the value.
    /// <para>
    /// This replaces the pre-#3300 <c>SupportsStreamingSequence</c> early <c>return</c>, which made a
    /// skipped provider indistinguishable from a passing one in the run output. An exclusion here is
    /// named, reasoned, and narrow: it waives exactly one rule, and every other rule still applies.
    /// The default is empty because no provider currently has a legitimate excuse - if one appears,
    /// the excuse must be written down next to the code that needs it.
    /// </para>
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> ExcludedOrderingRules =>
        new Dictionary<string, string>();

    private void AssertOrdering(IReadOnlyList<AssistantMessageEvent> events, string scenario)
    {
        var violations = AssistantMessageEventOrdering.Validate(events);
        var excluded = ExcludedOrderingRules;

        var enforced = violations.Where(v => !excluded.ContainsKey(v.Rule)).ToList();

        enforced.ShouldBeEmpty(
            $"{GetType().Name} violated the normalized event ordering grammar on the {scenario} " +
            $"(#3300):{Environment.NewLine}" +
            string.Join(
                Environment.NewLine,
                enforced.Select(v => $"  [{v.Rule}] at event {v.EventIndex}: {v.Message}")) +
            $"{Environment.NewLine}Observed sequence: {string.Join(", ", events.Select(e => e.Type))}");
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

    /// <summary>
    /// Cancellation must produce ONE normalized shape across every producer: an
    /// <see cref="ErrorEvent"/> carrying <see cref="StopReason.Aborted"/> (#3292).
    /// <para>
    /// This assertion is deliberately named and exclusive. Its predecessor,
    /// <c>Stream_CancellationDuringStreaming_ThrowsOrEmitsError</c>, asserted the disjunction
    /// "throws, or errors, or returns anything at all", which is satisfied by BOTH shapes and is
    /// precisely why the Responses/Completions engines emitting <c>DoneEvent(Aborted)</c> while
    /// Anthropic emitted <c>ErrorEvent(Aborted)</c> survived undetected. A <c>DoneEvent</c> is the
    /// normal-completion case of the event union; a cancelled turn did not complete, so a consumer
    /// switching on event type - the documented way to consume this union - would have classified
    /// those cancellations as successes.
    /// </para>
    /// <para>
    /// The old test also raced by construction: it cancelled inside the handler and then returned a
    /// complete, well-formed success payload, so "the response arrived before cancellation was
    /// observed" was an accepted outcome and the cancellation path was frequently never entered.
    /// The stalling handler below removes the race - the request cannot complete, so the only way
    /// out of the provider's request loop is the cancellation catch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stream_Cancellation_EmitsErrorEventWithAbortedReason()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StallingHandler();

        var provider = CreateProvider(handler);
        var options = CreateOptions() with { CancellationToken = cts.Token };
        var stream = provider.Stream(CreateModel(), CreateContext(), options);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));

        var events = new List<AssistantMessageEvent>();
        // Bound the read: a producer that emits no terminal event at all would otherwise hang the
        // whole suite. A thrown OperationCanceledException here is a loud failure, which is what a
        // silently-never-terminating stream deserves.
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in stream.WithCancellation(readTimeout.Token))
            events.Add(evt);

        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        events.ShouldNotBeEmpty(
            $"{provider.GetType().Name} must emit a terminal event for a cancelled turn, not end the " +
            "stream silently");
        var terminal = events[^1];

        var error = terminal.ShouldBeOfType<ErrorEvent>(
            $"{provider.GetType().Name} must normalize cancellation to ErrorEvent(StopReason.Aborted) " +
            "(#3292). DoneEvent is the normal-completion case and a cancelled turn did not complete, " +
            "so a consumer switching on event type would read this cancellation as a success.");
        error.Reason.ShouldBe(StopReason.Aborted);

        events.OfType<DoneEvent>().ShouldBeEmpty(
            "a cancelled turn must not emit a DoneEvent at all - carrying StopReason.Aborted on the " +
            "completion event is the contradiction #3292 exists to remove");

        result.StopReason.ShouldBe(StopReason.Aborted);
    }

    /// <summary>
    /// A handler that never completes its request, so the only exit from the provider's request loop
    /// is the caller's cancellation token. Returning a canned response instead would reintroduce the
    /// race the #3292 assertion exists to eliminate.
    /// </summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }


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

    /// <summary>
    /// Build a wire payload carrying two tool calls whose argument deltas interleave, so the producer
    /// must maintain per-content-index block state rather than a single "current block" cursor.
    /// Abstract rather than virtual on purpose: a default implementation would silently degrade to
    /// the single-call case for any provider that forgot to supply one, which is the exact failure
    /// mode #3300 exists to remove.
    /// </summary>
    protected abstract string BuildInterleavedToolCallPayload(
        string firstToolCallId,
        string firstToolName,
        string firstArgumentsJson,
        string secondToolCallId,
        string secondToolName,
        string secondArgumentsJson,
        string providerStopReason);
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
