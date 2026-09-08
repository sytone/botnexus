using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Cross-API coverage for safety-refusal normalization (#3295).
/// </summary>
/// <remarks>
/// Before this fix a refusal had no normalized representation at all. The Chat Completions parser
/// read <c>delta.refusal</c> only to set the stop reason and dropped the string, so a refused turn
/// rendered as an empty assistant message; the Responses parser pushed refusal deltas down the
/// ordinary text channel, so a refusal was indistinguishable from model prose until the terminal
/// event arrived - after the text had already been streamed to the user.
///
/// These tests drive real SSE bytes through both parsers, because the defect was in the event
/// loops, not in a helper: only a byte-level drive can detect the refusal channel being re-merged
/// into the text channel.
/// </remarks>
public class RefusalContentNormalizationTests
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

    private const string RefusalText = "I can't help with that.";

    /// <summary>
    /// A Chat Completions refusal turn: refusal fragments on <c>delta.refusal</c>, terminated by
    /// <c>finish_reason: refusal</c>. No <c>delta.content</c> is ever sent - which is exactly why
    /// dropping the refusal string produced an empty message.
    /// </summary>
    private static string CompletionsRefusalSse()
    {
        var builder = new StringBuilder();
        foreach (var fragment in new[] { "I can't ", "help with that." })
        {
            builder.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                id = "chatcmpl_1",
                choices = new[] { new { index = 0, delta = new { refusal = fragment } } }
            })).Append('\n');
        }

        builder.Append("data: ").Append(JsonSerializer.Serialize(new
        {
            id = "chatcmpl_1",
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "refusal" } }
        })).Append('\n');
        builder.Append("data: [DONE]\n");
        return builder.ToString();
    }

    /// <summary>The Responses-API equivalent of <see cref="CompletionsRefusalSse"/>.</summary>
    private static string ResponsesRefusalSse()
    {
        var builder = new StringBuilder();
        builder.Append("event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n");
        builder.Append(
            "event: response.output_item.added\n" +
            "data: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n");

        foreach (var fragment in new[] { "I can't ", "help with that." })
        {
            builder.Append("event: response.refusal.delta\ndata: {\"item_id\":\"item_1\",\"delta\":");
            builder.Append(JsonSerializer.Serialize(fragment));
            builder.Append("}\n\n");
        }

        builder.Append(
            "event: response.output_item.done\n" +
            "data: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n");
        builder.Append(
            "event: response.completed\n" +
            "data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n");
        return builder.ToString();
    }

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
            mapStopReason: reason => reason switch
            {
                "refusal" => (StopReason.Refusal, (string?)null),
                "tool_calls" => (StopReason.ToolUse, null),
                _ => (StopReason.Stop, null)
            },
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
            ct: CancellationToken.None);

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        return (events, await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // AC2. The regression that made a refused Completions turn render as an empty bubble. The
    // assertion is on the refusal STRING surviving into content, not merely on a block existing.
    [Fact]
    public async Task Completions_RefusalDelta_EmitsRefusalStringAsContent()
    {
        var (_, final) = await RunCompletionsAsync(CompletionsRefusalSse());

        var refusals = final.Content.OfType<RefusalContent>().ToList();
        refusals.Count.ShouldBe(1);
        refusals[0].Text.ShouldBe(RefusalText);
    }

    // AC1. Distinguishability must not depend on the terminal stop reason, so this assertion
    // deliberately reads only the mid-stream delta events and the content block's own type.
    [Fact]
    public async Task Responses_RefusalDelta_IsDistinguishableFromTextWithoutStopReason()
    {
        var (events, final) = await RunResponsesAsync(ResponsesRefusalSse());

        var refusals = final.Content.OfType<RefusalContent>().ToList();
        refusals.Count.ShouldBe(1);
        refusals[0].Text.ShouldBe(RefusalText);

        // Not a plain TextContent masquerading as prose: every content block on a pure refusal
        // turn is a refusal block.
        final.Content.ShouldAllBe(block => block is RefusalContent);

        // The distinguishing evidence is present on the partial carried by the delta events,
        // i.e. at the moment the text is streamed - before any terminal event exists.
        var deltas = events.OfType<TextDeltaEvent>().ToList();
        deltas.ShouldNotBeEmpty();
        deltas.ShouldAllBe(d => d.Partial.Content.OfType<RefusalContent>().Any());
    }

    // AC1, negative half. Without this, "everything is a refusal" would satisfy the test above.
    [Fact]
    public async Task Responses_OrdinaryTextDelta_IsNotClassifiedAsRefusal()
    {
        var sse =
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_item.added\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"Sure, here you go.\"}\n\n" +
            "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n";

        var (_, final) = await RunResponsesAsync(sse);

        final.Content.OfType<RefusalContent>().ShouldBeEmpty();
        final.Content.OfType<TextContent>().Single().Text.ShouldBe("Sure, here you go.");
        final.StopReason.ShouldBe(StopReason.Stop);
    }

    // AC2, negative half for the Completions path.
    [Fact]
    public async Task Completions_OrdinaryContentDelta_IsNotClassifiedAsRefusal()
    {
        var sse =
            "data: " + JsonSerializer.Serialize(new
            {
                id = "chatcmpl_1",
                choices = new[] { new { index = 0, delta = new { content = "Sure, here you go." } } }
            }) + "\n" +
            "data: [DONE]\n";

        var (_, final) = await RunCompletionsAsync(sse);

        final.Content.OfType<RefusalContent>().ShouldBeEmpty();
        final.Content.OfType<TextContent>().Single().Text.ShouldBe("Sure, here you go.");
    }

    // AC3. The pre-existing terminal signal is unchanged on both paths.
    [Fact]
    public async Task BothPaths_StillSetRefusalStopReasonOnTerminalMessage()
    {
        var (completionsEvents, completionsFinal) = await RunCompletionsAsync(CompletionsRefusalSse());
        var (responsesEvents, responsesFinal) = await RunResponsesAsync(ResponsesRefusalSse());

        completionsFinal.StopReason.ShouldBe(StopReason.Refusal);
        responsesFinal.StopReason.ShouldBe(StopReason.Refusal);
        completionsEvents.OfType<DoneEvent>().Single().Reason.ShouldBe(StopReason.Refusal);
        responsesEvents.OfType<DoneEvent>().Single().Reason.ShouldBe(StopReason.Refusal);
    }

    // AC4. The point of the issue: one refusal, two APIs, one normalized shape. Asserting the
    // two shapes are equal to EACH OTHER (rather than each to a hand-written literal) is what
    // makes a future divergence on either parser fail this test.
    [Fact]
    public async Task RefusalTurn_ProducesIdenticalNormalizedShapeAcrossBothApis()
    {
        var (_, completionsFinal) = await RunCompletionsAsync(CompletionsRefusalSse());
        var (_, responsesFinal) = await RunResponsesAsync(ResponsesRefusalSse());

        static IReadOnlyList<(string Kind, string Text)> Shape(AssistantMessage message) =>
            message.Content.Select(block => (
                Kind: block switch
                {
                    RefusalContent => "refusal",
                    TextContent => "text",
                    ThinkingContent => "thinking",
                    ToolCallContent => "toolCall",
                    _ => block.GetType().Name
                },
                Text: block is TextContent text ? text.Text : string.Empty)).ToList();

        var completionsShape = Shape(completionsFinal);
        var responsesShape = Shape(responsesFinal);

        // Guard against the comparison passing vacuously on two empty lists - the exact failure
        // mode that existed before this fix on the Completions side.
        completionsShape.ShouldBe([("refusal", RefusalText)]);
        responsesShape.ShouldBe(completionsShape);
        responsesFinal.StopReason.ShouldBe(completionsFinal.StopReason);
    }

    // A refusal block must remain readable as text by every existing consumer, all of which
    // project content via OfType&lt;TextContent&gt;(). This is the property that lets the fix be
    // additive instead of a breaking change across ~30 files.
    [Fact]
    public async Task RefusalContent_IsReadableByExistingTextConsumers()
    {
        var (_, final) = await RunCompletionsAsync(CompletionsRefusalSse());

        string.Concat(final.Content.OfType<TextContent>().Select(t => t.Text)).ShouldBe(RefusalText);
    }

    // Polymorphic round-trip: refusal must survive session persistence with its own discriminator
    // rather than silently degrading to "text" on the way back out of the store.
    [Fact]
    public void RefusalContent_RoundTripsThroughPolymorphicJsonWithItsOwnDiscriminator()
    {
        ContentBlock block = new RefusalContent(RefusalText);

        var json = JsonSerializer.Serialize(block);
        json.ShouldContain("\"refusal\"");

        var restored = JsonSerializer.Deserialize<ContentBlock>(json);
        restored.ShouldBeOfType<RefusalContent>();
        ((RefusalContent)restored!).Text.ShouldBe(RefusalText);
    }

    // Record equality must not collapse the two kinds: a refusal and a prose block with the same
    // characters are different normalized content, which is the whole point of AC1.
    [Fact]
    public void RefusalContent_IsNotEqualToTextContentWithSameText()
    {
        new RefusalContent(RefusalText).ShouldNotBe(new TextContent(RefusalText));
    }
}
