using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Cross-API parity for provider content filtering (#3296): the same upstream condition must
/// normalize to the same <see cref="StopReason"/> whichever API served the turn.
/// </summary>
/// <remarks>
/// Before this fix the Chat Completions engine mapped <c>finish_reason: "content_filter"</c> to
/// <see cref="StopReason.Error"/> while the Responses parser mapped the equivalent
/// <c>incomplete_details.reason == "content_filter"</c> - and the <c>content_filter</c> response
/// status - to <see cref="StopReason.Sensitive"/>. That is not a cosmetic difference: the agent
/// loop terminates a run on <c>Error</c> but not on <c>Sensitive</c>, and any error-rate signal
/// derived from <c>Error</c> counted a safety decision as an infrastructure failure.
/// <para>
/// The tests drive real SSE bytes through both parsers rather than calling the mapping helpers in
/// isolation, and the parity assertion compares the two APIs to EACH OTHER rather than each to a
/// literal, so a future divergence introduced on either parser fails here.
/// </para>
/// </remarks>
public class ContentFilterStopReasonParityTests
{
    private static LlmModel CompletionsModel() => new(
        Id: "gpt-4o",
        Name: "GPT-4o",
        Api: "openai-completions",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 16384);

    private static LlmModel ResponsesModel() => new(
        Id: "gpt-5",
        Name: "GPT-5",
        Api: "openai-responses",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 200000,
        MaxTokens: 16384);

    /// <summary>
    /// A Chat Completions turn whose partial text is cut off by the provider's content filter.
    /// </summary>
    private static string CompletionsContentFilterSse()
    {
        var builder = new StringBuilder();
        builder.Append("data: ").Append(JsonSerializer.Serialize(new
        {
            id = "chatcmpl_1",
            choices = new[] { new { index = 0, delta = new { content = "Here is how you " } } }
        })).Append('\n');
        builder.Append("data: ").Append(JsonSerializer.Serialize(new
        {
            id = "chatcmpl_1",
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "content_filter" } }
        })).Append('\n');
        builder.Append("data: [DONE]\n");
        return builder.ToString();
    }

    /// <summary>The Responses-API equivalent: an incomplete response whose reason is the filter.</summary>
    private static string ResponsesIncompleteContentFilterSse() =>
        "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
        "event: response.output_item.added\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
        "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"Here is how you \"}\n\n" +
        "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
        "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"incomplete\"," +
        "\"incomplete_details\":{\"reason\":\"content_filter\"}}}\n\n";

    /// <summary>The other Responses shape: the filter surfaced as the response STATUS itself.</summary>
    private static string ResponsesContentFilterStatusSse() =>
        "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
        "event: response.output_item.added\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
        "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"Here is how you \"}\n\n" +
        "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
        "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"content_filter\"}}\n\n";

    /// <summary>
    /// Drives the Completions parser with the REAL production mapping
    /// (<see cref="CompletionsStreamEngine.MapStopReason"/>). Substituting a test-local lambda here
    /// would make the whole file vacuous - the mapping is the thing under test.
    /// </summary>
    private static async Task<(List<AssistantMessageEvent> Events, AssistantMessage Final)> RunCompletionsAsync(string sse)
    {
        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));

        await new OpenAIStreamProcessor().ParseOpenAiCompletionsAsync(
            stream,
            reader,
            CompletionsModel(),
            api: "openai-completions",
            parseUsage: (_, usage, _) => usage,
            mapStopReason: CompletionsStreamEngine.MapStopReason,
            extractProviderErrorMessage: (raw, _) => raw,
            emitError: (_, _, _, _) => { },
            onMalformedChunk: null,
            ct: CancellationToken.None);

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        return (events, await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async Task<(List<AssistantMessageEvent> Events, AssistantMessage Final)> RunResponsesAsync(string sse)
    {
        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));

        await ResponsesStreamParser.ParseAsync(
            stream,
            reader,
            ResponsesModel(),
            options: null,
            api: "openai-responses",
            logger: NullLogger.Instance,
            emitError: (_, _, _, _) => { },
            onParsedEvent: null,
            resolveConfiguredServiceTier: null,
            normalizeTextDelta: null,
            ct: CancellationToken.None);

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        return (events, await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // AC1. The mapping itself, named so the regression is legible without reading a fixture.
    [Fact]
    public void MapStopReason_ContentFilter_IsSensitiveNotError()
    {
        var (stopReason, _) = CompletionsStreamEngine.MapStopReason("content_filter");

        stopReason.ShouldBe(
            StopReason.Sensitive,
            "content filtering is a safety outcome, not an infrastructure failure (#3296); " +
            "StopReason.Sensitive is the purpose-built value and mapping it to Error made half " +
            "the platform unable to reach the enum member at all");
        stopReason.ShouldNotBe(StopReason.Error);
    }

    // AC4. The human-readable message is load-bearing: a filtered turn's surviving text is a
    // fragment, so this string is the only statement of WHY it stopped.
    [Fact]
    public void MapStopReason_ContentFilter_PreservesHumanReadableMessage()
    {
        var (_, message) = CompletionsStreamEngine.MapStopReason("content_filter");

        message.ShouldBe("Content filtered by provider");
    }

    // AC1, sad path. The remap must be surgical: the reasons that legitimately mean "provider
    // failure" must still be Error, or the fix would have traded one misattribution for another.
    [Theory]
    [InlineData("network_error")]
    [InlineData("some_unknown_reason")]
    public void MapStopReason_GenuineFailureReasons_RemainError(string reason)
    {
        CompletionsStreamEngine.MapStopReason(reason).StopReason.ShouldBe(StopReason.Error);
    }

    // AC1, end-to-end through the real parser rather than the helper.
    [Fact]
    public async Task Completions_ContentFilteredTurn_TerminatesWithSensitive()
    {
        var (events, final) = await RunCompletionsAsync(CompletionsContentFilterSse());

        final.StopReason.ShouldBe(StopReason.Sensitive);
        events.OfType<DoneEvent>().Single().Reason.ShouldBe(StopReason.Sensitive);
    }

    // AC4 end-to-end: the message survives onto the terminal assistant message, not just out of
    // the mapping helper.
    [Fact]
    public async Task Completions_ContentFilteredTurn_CarriesTheProviderMessage()
    {
        var (_, final) = await RunCompletionsAsync(CompletionsContentFilterSse());

        final.ErrorMessage.ShouldBe("Content filtered by provider");
    }

    // AC2, both Responses shapes.
    [Fact]
    public async Task Responses_ContentFilteredTurn_TerminatesWithSensitive()
    {
        var (_, incompleteDetails) = await RunResponsesAsync(ResponsesIncompleteContentFilterSse());
        var (_, statusForm) = await RunResponsesAsync(ResponsesContentFilterStatusSse());

        incompleteDetails.StopReason.ShouldBe(StopReason.Sensitive);
        statusForm.StopReason.ShouldBe(StopReason.Sensitive);
    }

    // AC2, the parity assertion proper. Comparing the two APIs to each other - and separately
    // pinning the shared value - is what makes a future divergence on EITHER parser fail, while
    // the explicit Sensitive check stops the comparison passing because both regressed together.
    [Fact]
    public async Task ContentFilteredTurn_ProducesTheSameStopReasonOnBothApis()
    {
        var (_, completionsFinal) = await RunCompletionsAsync(CompletionsContentFilterSse());
        var (_, responsesFinal) = await RunResponsesAsync(ResponsesIncompleteContentFilterSse());

        completionsFinal.StopReason.ShouldBe(
            responsesFinal.StopReason,
            "the same upstream content-filter decision must normalize to one StopReason " +
            "regardless of which API served the turn (#3296)");
        completionsFinal.StopReason.ShouldBe(StopReason.Sensitive);
    }

    // Sad path / non-vacuity: an ordinary completed turn on either API must NOT be Sensitive, so
    // "everything is Sensitive" cannot satisfy the parity test above.
    [Fact]
    public async Task OrdinaryTurn_IsNotClassifiedAsSensitiveOnEitherApi()
    {
        var completionsSse =
            "data: " + JsonSerializer.Serialize(new
            {
                id = "chatcmpl_1",
                choices = new[] { new { index = 0, delta = new { content = "Sure, here you go." } } }
            }) + "\n" +
            "data: " + JsonSerializer.Serialize(new
            {
                id = "chatcmpl_1",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            }) + "\n" +
            "data: [DONE]\n";

        var responsesSse =
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_item.added\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"Sure, here you go.\"}\n\n" +
            "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n";

        var (_, completionsFinal) = await RunCompletionsAsync(completionsSse);
        var (_, responsesFinal) = await RunResponsesAsync(responsesSse);

        completionsFinal.StopReason.ShouldBe(StopReason.Stop);
        responsesFinal.StopReason.ShouldBe(StopReason.Stop);
    }

    // Sad path: a truncation is not a filter. Length and Sensitive are different outcomes and the
    // Responses "incomplete" status without a content_filter reason must stay Length.
    [Fact]
    public async Task IncompleteWithoutContentFilterReason_StaysLength()
    {
        var sse =
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_item.added\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"Here is how you \"}\n\n" +
            "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"incomplete\"," +
            "\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n";

        var (_, final) = await RunResponsesAsync(sse);

        final.StopReason.ShouldBe(StopReason.Length);
    }
}
