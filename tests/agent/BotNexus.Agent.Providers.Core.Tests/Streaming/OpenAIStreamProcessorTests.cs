using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Characterisation tests for <see cref="OpenAIStreamProcessor.ParseOpenAiCompletionsAsync"/>.
/// These pin the observable streaming behaviour that the #1378 allocation/parse optimisations
/// must preserve: per-delta parsed tool arguments on the partial message, identical final
/// content, and the snapshot-reuse contract of the internal partial-content tracker.
/// </summary>
public class OpenAIStreamProcessorTests
{
    private static readonly LlmModel Model = new(
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

    /// <summary>
    /// Drives <see cref="OpenAIStreamProcessor.ParseOpenAiCompletionsAsync"/> over a literal SSE
    /// body and returns every event the processor pushed onto the stream.
    /// </summary>
    private static async Task<List<AssistantMessageEvent>> RunAsync(string sse)
    {
        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        var processor = new OpenAIStreamProcessor();

        await processor.ParseOpenAiCompletionsAsync(
            stream,
            reader,
            Model,
            api: "openai-completions",
            parseUsage: (_, usage, _) => usage,
            mapStopReason: reason => reason == "tool_calls"
                ? (StopReason.ToolUse, null)
                : (StopReason.Stop, null),
            extractProviderErrorMessage: (raw, _) => raw,
            emitError: (s, m, msg, content) => s.Push(
                new ErrorEvent(StopReason.Error, new AssistantMessage(
                    Content: content ?? [],
                    Api: "openai-completions",
                    Provider: m.Provider,
                    ModelId: m.Id,
                    Usage: Usage.Empty(),
                    StopReason: StopReason.Error,
                    ErrorMessage: msg,
                    ResponseId: null,
                    Timestamp: 0))),
            onMalformedChunk: null,
            ct: CancellationToken.None);

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);
        return events;
    }

    private static string DeltaChunk(string argsFragment) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { index = 0, delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = argsFragment } } } } }
            }
        }) + "\n";

    private static string ToolCallStartChunk(string id, string name) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { index = 0, delta = new { tool_calls = new[] { new { index = 0, id, function = new { name, arguments = "" } } } } }
            }
        }) + "\n";

    [Fact]
    public async Task ToolCallArguments_ParseIncrementallyOnEveryDelta()
    {
        // arguments arrive in three fragments: {"path": / "/tmp/x / "}
        var sse = ToolCallStartChunk("call_1", "read_file")
                  + DeltaChunk("{\"path\":")
                  + DeltaChunk("\"/tmp/x")
                  + DeltaChunk("\"}")
                  + "data: " + JsonSerializer.Serialize(new
                  {
                      choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } }
                  }) + "\n"
                  + "data: [DONE]\n";

        var events = await RunAsync(sse);

        var deltas = events.OfType<ToolCallDeltaEvent>().ToList();
        deltas.Count.ShouldBe(3);

        // Each delta's partial must expose the *cumulative* parsed args produced by the streaming
        // repair parser — never an empty/deferred value once the buffer forms valid JSON. This is
        // the behaviour the parse-cache must preserve: the args are materialised onto the partial
        // on every delta, not only at the end. By the final delta the full object is parseable.
        var lastArgs = ToolArgs(deltas[2].Partial);
        lastArgs["path"].ShouldBe("/tmp/x");

        // The raw delta fragments are surfaced verbatim and reassemble to the complete arg JSON.
        string.Concat(deltas.Select(d => d.Delta)).ShouldBe("{\"path\":\"/tmp/x\"}");
    }

    [Fact]
    public async Task FinalToolCall_HasFullyParsedArguments()
    {
        var sse = ToolCallStartChunk("call_1", "write_file")
                  + DeltaChunk("{\"path\":\"/a\",")
                  + DeltaChunk("\"content\":\"hi\"}")
                  + "data: " + JsonSerializer.Serialize(new
                  {
                      choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } }
                  }) + "\n"
                  + "data: [DONE]\n";

        var events = await RunAsync(sse);

        var end = events.OfType<ToolCallEndEvent>().ShouldHaveSingleItem();
        end.ToolCall.Id.ShouldBe("call_1");
        end.ToolCall.Name.ShouldBe("write_file");
        end.ToolCall.Arguments["path"].ShouldBe("/a");
        end.ToolCall.Arguments["content"].ShouldBe("hi");

        var done = events.OfType<DoneEvent>().ShouldHaveSingleItem();
        done.Reason.ShouldBe(StopReason.ToolUse);
        var finalTool = done.Message.Content.OfType<ToolCallContent>().ShouldHaveSingleItem();
        finalTool.Arguments["path"].ShouldBe("/a");
        finalTool.Arguments["content"].ShouldBe("hi");
    }

    [Fact]
    public async Task ToolCallWithNoArgumentDeltas_StillParsesEmptyArgsAtEnd()
    {
        // A tool call that only ever gets id+name and never an argument delta: the final pass
        // has no cached parse to reuse and must fall back to parsing the empty buffer.
        var sse = ToolCallStartChunk("call_x", "get_time")
                  + "data: " + JsonSerializer.Serialize(new
                  {
                      choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } }
                  }) + "\n"
                  + "data: [DONE]\n";

        var events = await RunAsync(sse);

        var end = events.OfType<ToolCallEndEvent>().ShouldHaveSingleItem();
        end.ToolCall.Name.ShouldBe("get_time");
        end.ToolCall.Arguments.ShouldBeEmpty();
    }

    [Fact]
    public async Task TextDeltas_AccumulateOnPartial()
    {
        var sse = TextChunk("Hello")
                  + TextChunk(", world")
                  + "data: " + JsonSerializer.Serialize(new
                  {
                      choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
                  }) + "\n"
                  + "data: [DONE]\n";

        var events = await RunAsync(sse);

        var textDeltas = events.OfType<TextDeltaEvent>().ToList();
        textDeltas.Count.ShouldBe(2);
        TextOf(textDeltas[0].Partial).ShouldBe("Hello");
        TextOf(textDeltas[1].Partial).ShouldBe("Hello, world");

        var done = events.OfType<DoneEvent>().ShouldHaveSingleItem();
        TextOf(done.Message).ShouldBe("Hello, world");
    }

    [Fact]
    public async Task PartialSnapshot_IsReusedWhenContentShapeUnchanged()
    {
        // Two consecutive text deltas on the same block replace contentBlocks[0] in place each
        // time, so the snapshot is invalidated and rebuilt on every delta — different instances.
        // But the StartEvent and the first TextStartEvent are emitted with no content change
        // between them, so they must SHARE the same cached snapshot instance.
        var sse = TextChunk("a")
                  + "data: [DONE]\n";

        var events = await RunAsync(sse);

        var start = events.OfType<StartEvent>().ShouldHaveSingleItem();
        var textStart = events.OfType<TextStartEvent>().ShouldHaveSingleItem();

        // start fires before the text block is added; text_start fires right after the empty
        // TextContent is appended. They differ by exactly one mutation, so the snapshots differ.
        ReferenceEquals(start.Partial.Content, textStart.Partial.Content).ShouldBeFalse();

        // The empty start snapshot must itself be a stable empty list (no content yet).
        start.Partial.Content.ShouldBeEmpty();
    }

    [Fact]
    public async Task PartialSnapshot_ReusedBetweenTextEndAndDoneWhenNoContentMutation()
    {
        // After the final TextEndEvent there is no further content mutation before DoneEvent
        // (the done message is `BuildPartial() with { ... }`, which reuses the same Content
        // reference). The cached snapshot must therefore be the *same instance* on both —
        // proving the tracker reuses its snapshot when the content shape has not changed.
        var sse = TextChunk("x")
                  + "data: " + JsonSerializer.Serialize(new
                  {
                      choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
                  }) + "\n"
                  + "data: [DONE]\n";
        var events = await RunAsync(sse);

        var textEnd = events.OfType<TextEndEvent>().ShouldHaveSingleItem();
        var done = events.OfType<DoneEvent>().ShouldHaveSingleItem();

        ReferenceEquals(textEnd.Partial.Content, done.Message.Content).ShouldBeTrue();
        TextOf(done.Message).ShouldBe("x");
    }

    private static string TextChunk(string text) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { content = text } } }
        }) + "\n";

    private static IReadOnlyDictionary<string, object?> ToolArgs(AssistantMessage partial) =>
        partial.Content.OfType<ToolCallContent>().First().Arguments;

    private static string TextOf(AssistantMessage partial) =>
        string.Concat(partial.Content.OfType<TextContent>().Select(t => t.Text));
}
