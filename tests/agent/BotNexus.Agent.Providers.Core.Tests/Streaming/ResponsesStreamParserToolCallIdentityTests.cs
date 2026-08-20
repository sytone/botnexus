using System.Text;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Identity on streaming tool-call events for the unified Responses parser (#3290).
/// <para>
/// The Responses parser is driven directly here rather than through a provider, because it is the
/// producer whose <c>ContentIndex</c> is allocated as <c>contentBlocks.Count</c> over every block -
/// text and reasoning included - and is therefore the clearest case where an index is not a position
/// in <c>AssistantMessage.ToolCalls</c>. A consumer resolving identity by index, as
/// <c>StreamAccumulator</c> did before #3290, reads the wrong call or falls off the end of the list
/// and guesses "the most recent one".
/// </para>
/// <para>
/// The id carried on the events is the composed <c>callId|itemId</c>, the same value the content
/// block and the end event use, so a consumer correlates start, delta and end with a single key.
/// </para>
/// </summary>
public class ResponsesStreamParserToolCallIdentityTests
{
    [Fact]
    public async Task ParseAsync_TwoToolCallsAfterAReasoningBlock_EventsCarryTheirOwnIdentity()
    {
        // A reasoning block first, deliberately: it consumes content index 0, so the two tool calls
        // land at content indices 1 and 2 while occupying ToolCalls positions 0 and 1. That offset is
        // precisely what made the old index-based lookup wrong.
        var sse =
            "event: response.created\n" +
            "data: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_item.added\n" +
            "data: {\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\"}}\n\n" +
            "event: response.output_item.added\n" +
            "data: {\"item\":{\"type\":\"function_call\",\"call_id\":\"call_a\",\"id\":\"fc_a\",\"name\":\"search\",\"arguments\":\"\"}}\n\n" +
            "event: response.output_item.added\n" +
            "data: {\"item\":{\"type\":\"function_call\",\"call_id\":\"call_b\",\"id\":\"fc_b\",\"name\":\"lookup\",\"arguments\":\"\"}}\n\n" +
            "event: response.function_call_arguments.delta\n" +
            "data: {\"call_id\":\"call_a\",\"item_id\":\"fc_a\",\"delta\":\"{\\\"query\\\":\\\"weather\\\"}\"}\n\n" +
            "event: response.function_call_arguments.delta\n" +
            "data: {\"call_id\":\"call_b\",\"item_id\":\"fc_b\",\"delta\":\"{\\\"id\\\":\\\"42\\\"}\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n";

        var events = await ParseAsync(sse);

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        starts.Count.ShouldBe(2, "both function_call items must open a tool call block");
        starts.Select(s => s.ToolCallId).ShouldBe(new[] { "call_a|fc_a", "call_b|fc_b" }, ignoreOrder: true);
        starts.Select(s => s.ToolName).ShouldBe(new[] { "search", "lookup" }, ignoreOrder: true);

        // The start events' content indices are not ToolCalls positions - this is the offset the old
        // index-based resolution silently mis-read, and asserting it keeps the test honest about why
        // carrying the id matters.
        starts.Select(s => s.ContentIndex).ShouldBe(new[] { 1, 2 }, ignoreOrder: true);

        var deltas = events.OfType<ToolCallDeltaEvent>().ToList();
        deltas.Count.ShouldBe(2, "each call contributed exactly one argument fragment");

        var aDelta = deltas.Where(d => d.Delta.Contains("weather")).ShouldHaveSingleItem();
        aDelta.ToolCallId.ShouldBe("call_a|fc_a",
            "the 'weather' fragment belongs to call_a; attributing it elsewhere is the #3290 defect");
        aDelta.ToolName.ShouldBe("search");

        var bDelta = deltas.Where(d => d.Delta.Contains("42")).ShouldHaveSingleItem();
        bDelta.ToolCallId.ShouldBe("call_b|fc_b",
            "the 'id: 42' fragment belongs to call_b; attributing it elsewhere is the #3290 defect");
        bDelta.ToolName.ShouldBe("lookup");
    }

    /// <summary>
    /// A <c>function_call</c> item that already carries its arguments emits an immediate delta. That
    /// delta is produced on a different code path from the incremental one above and must carry the
    /// same identity - a producer is only fixed when every one of its emit sites is.
    /// </summary>
    [Fact]
    public async Task ParseAsync_ToolCallWithInlineArguments_ImmediateDeltaCarriesIdentity()
    {
        var sse =
            "event: response.created\n" +
            "data: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_item.added\n" +
            "data: {\"item\":{\"type\":\"function_call\",\"call_id\":\"call_inline\",\"id\":\"fc_inline\",\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"/tmp/x\\\"}\"}}\n\n" +
            "event: response.completed\n" +
            "data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n";

        var events = await ParseAsync(sse);

        var start = events.OfType<ToolCallStartEvent>().ShouldHaveSingleItem();
        start.ToolCallId.ShouldBe("call_inline|fc_inline");
        start.ToolName.ShouldBe("read_file");

        var delta = events.OfType<ToolCallDeltaEvent>().ShouldHaveSingleItem();
        delta.ToolCallId.ShouldBe("call_inline|fc_inline");
        delta.ToolName.ShouldBe("read_file");
    }

    private static async Task<List<AssistantMessageEvent>> ParseAsync(string sse)
    {
        var stream = new LlmStream();
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));

        var parse = ResponsesStreamParser.ParseAsync(
            stream,
            reader,
            Model(),
            options: null,
            api: "openai-responses",
            logger: NullLogger.Instance,
            emitError: (_, _, _, _) => { },
            onParsedEvent: null,
            resolveConfiguredServiceTier: null,
            normalizeTextDelta: null,
            ct: CancellationToken.None);

        var events = new List<AssistantMessageEvent>();
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var evt in stream.WithCancellation(readTimeout.Token))
            events.Add(evt);

        await parse;
        return events;
    }

    private static LlmModel Model() => new(
        Id: "gpt-5",
        Name: "GPT-5",
        Api: "openai-responses",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 200000,
        MaxTokens: 16384);
}
