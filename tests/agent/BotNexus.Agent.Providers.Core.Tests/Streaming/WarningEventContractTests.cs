using System.Text;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Contract tests for the non-terminal <see cref="WarningEvent"/> (#3291).
/// </summary>
/// <remarks>
/// Two properties are under test and they are not the same property. The first is that
/// <see cref="LlmStream.Push"/> does not treat a warning as terminal - a warning that ends the
/// stream is an error with a friendlier name. The second is that the two known silent sites
/// actually emit one: a contract member no producer emits is a vacuous change, so each producer
/// assertion drives real bytes through the real parser rather than constructing the event by hand.
/// </remarks>
public class WarningEventContractTests
{
    private static AssistantMessage MakeMessage() => new(
        Content: [],
        Api: "test-api",
        Provider: "test",
        ModelId: "test-model",
        Usage: Usage.Empty(),
        StopReason: StopReason.Stop,
        ErrorMessage: null,
        ResponseId: null,
        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static LlmModel Model() => new(
        Id: "gpt-5.6",
        Name: "GPT-5.6",
        Api: "openai-responses",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 200000,
        MaxTokens: 16384);

    // AC2: a warning followed by deltas and a DoneEvent yields every event, in order. If Push ever
    // gained a `case WarningEvent` that set _done or completed the writer, the deltas and the
    // DoneEvent would be dropped and this collapses to a single event.
    [Fact]
    public async Task WarningEvent_IsNotTerminal_SubsequentEventsAreStillObserved()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();

        stream.Push(new WarningEvent(WarningCodes.MalformedChunkSkipped, "skipped a frame", partial));
        stream.Push(new TextDeltaEvent(0, "hello", partial));
        stream.Push(new TextDeltaEvent(0, " world", partial));
        stream.Push(new DoneEvent(StopReason.Stop, partial));

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        events.Count.ShouldBe(4);
        events[0].ShouldBeOfType<WarningEvent>();
        events[1].ShouldBeOfType<TextDeltaEvent>();
        events[2].ShouldBeOfType<TextDeltaEvent>();
        events[3].ShouldBeOfType<DoneEvent>();
    }

    // AC2, second half: the result task must still be pending after a warning. A warning must not
    // capture a result, so a consumer awaiting the turn is not handed a premature answer.
    [Fact]
    public void WarningEvent_DoesNotCompleteResultTask()
    {
        var stream = new LlmStream();

        stream.Push(new WarningEvent(WarningCodes.StreamAssemblyMismatch, "mismatch", MakeMessage()));

        stream.GetResultAsync().IsCompleted.ShouldBeFalse();
    }

    // The event's Type discriminator is part of the wire-ish contract, not incidental.
    [Fact]
    public void WarningEvent_TypeDiscriminator_IsWarning()
        => new WarningEvent(WarningCodes.StreamAssemblyMismatch, "m", MakeMessage()).Type.ShouldBe("warning");

    // AC3: the assembly-mismatch detector's finding now leaves the provider layer. Driving real SSE
    // through the parser is what makes this fail if the `stream`/`buildPartial` arguments are
    // dropped from the Reconcile call site - a hand-built event could not detect that.
    [Fact]
    public async Task ResponsesParser_AssemblyMismatch_EmitsStreamAssemblyMismatchWarning()
    {
        var events = await RunResponsesAsync(
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"Hello\"}\n\n" +
            "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"\\r\\n world\"}\n\n" +
            "event: response.output_text.done\ndata: {\"item_id\":\"item_1\",\"text\":\"Hello world\"}\n\n" +
            "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n");

        var warning = events.OfType<WarningEvent>().ShouldHaveSingleItem();
        warning.Code.ShouldBe(WarningCodes.StreamAssemblyMismatch);

        // AC3, second clause: reporting the mismatch must not change the resolution. The provider's
        // final text is still preferred as canonical.
        events.OfType<DoneEvent>().ShouldHaveSingleItem()
            .Message.Content.OfType<TextContent>().Single().Text.ShouldBe("Hello world");
    }

    // AC6: the warning payload must carry no model or user content. The log line emits escaped
    // context windows around the divergence; the event deliberately does not, because it flows to
    // consumers and into persisted transcripts.
    [Fact]
    public async Task AssemblyMismatchWarning_CarriesNoModelOrUserContent()
    {
        const string Secret = "swordfish";

        var events = await RunResponsesAsync(
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            $"event: response.output_text.delta\ndata: {{\"item_id\":\"item_1\",\"delta\":\"{Secret}\\r\\n\"}}\n\n" +
            $"event: response.output_text.done\ndata: {{\"item_id\":\"item_1\",\"text\":\"{Secret}\\n\"}}\n\n" +
            "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n");

        var warning = events.OfType<WarningEvent>()
            .Single(w => w.Code == WarningCodes.StreamAssemblyMismatch);

        warning.Message.ShouldNotContain(Secret);
        // Positive half: proving absence alone would also pass for an empty message, so assert the
        // diagnostic value that IS meant to be there.
        warning.Message.ShouldContain("firstMismatchIndex=");
    }

    // AC4: the malformed-chunk skip path reports on the contract instead of only debug-logging.
    // Also proves non-termination end to end - the turn still completes with the good text.
    [Fact]
    public async Task ResponsesParser_MalformedChunk_EmitsMalformedChunkSkippedWarningAndContinues()
    {
        var events = await RunResponsesAsync(
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_text.delta\ndata: {not json at all\n\n" +
            "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"survived\"}\n\n" +
            "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n");

        events.OfType<WarningEvent>().ShouldHaveSingleItem()
            .Code.ShouldBe(WarningCodes.MalformedChunkSkipped);

        events.OfType<DoneEvent>().ShouldHaveSingleItem()
            .Message.Content.OfType<TextContent>().Single().Text.ShouldBe("survived");
    }

    // A clean stream must emit no warning at all. Without this the two assertions above would still
    // pass if the producer emitted a warning unconditionally, which would be worse than silence.
    [Fact]
    public async Task CleanResponsesStream_EmitsNoWarning()
    {
        var events = await RunResponsesAsync(
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"Hello world\"}\n\n" +
            "event: response.output_text.done\ndata: {\"item_id\":\"item_1\",\"text\":\"Hello world\"}\n\n" +
            "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n");

        events.OfType<WarningEvent>().ShouldBeEmpty();
    }

    // A warning is not a block event and must not perturb the normalized event grammar (#3300).
    [Fact]
    public async Task WarningEvent_DoesNotViolateEventOrdering()
    {
        var events = await RunResponsesAsync(
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_text.delta\ndata: {broken\n\n" +
            "event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":\"ok\"}\n\n" +
            "event: response.output_item.done\ndata: {\"item\":{\"type\":\"message\",\"id\":\"item_1\"}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n");

        events.OfType<WarningEvent>().ShouldNotBeEmpty();
        AssistantMessageEventOrdering.Validate(events).ShouldBeEmpty();
    }

    // The ordering grammar's start rule had to be stated as "nothing of substance precedes
    // StartEvent" rather than "StartEvent is at index 0" to admit a warning about the very first
    // frame. These two pin both halves: warnings are exempt, real events are still not.
    [Fact]
    public void EventOrdering_WarningBeforeStart_IsNotAViolation()
    {
        var partial = MakeMessage();
        AssistantMessageEventOrdering.Validate(new AssistantMessageEvent[]
        {
            new WarningEvent(WarningCodes.MalformedChunkSkipped, "first frame was malformed", partial),
            new StartEvent(partial),
            new DoneEvent(StopReason.Stop, partial)
        }).ShouldBeEmpty();
    }

    [Fact]
    public void EventOrdering_RealEventBeforeStart_IsStillAViolation()
    {
        var partial = MakeMessage();
        AssistantMessageEventOrdering.Validate(new AssistantMessageEvent[]
        {
            new TextStartEvent(0, partial),
            new StartEvent(partial),
            new TextEndEvent(0, "x", partial),
            new DoneEvent(StopReason.Stop, partial)
        }).Select(v => v.Rule)
          .ShouldContain(AssistantMessageEventOrdering.RuleStartPrecedesBlocks);
    }

    // AC4 on the Chat Completions transport specifically. The Responses test above covers the other
    // parser; both skip paths exist independently and a fix applied to only one transport is exactly
    // how #2170 reproduced #2049, so both are pinned.
    [Fact]
    public async Task CompletionsProcessor_MalformedChunk_EmitsWarningAndKeepsStreaming()
    {
        var sse =
            "data: {this is not json\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"survived\"}}]}\n" +
            "data: [DONE]\n";

        var model = new LlmModel(
            Id: "gpt-5.6",
            Name: "GPT-5.6",
            Api: "openai-completions",
            Provider: "openai",
            BaseUrl: "https://api.openai.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128000,
            MaxTokens: 16384);

        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));

        var captured = new List<AssistantMessageEvent>();
        var drain = Task.Run(async () =>
        {
            await foreach (var evt in stream)
                captured.Add(evt);
        });

        var callbackFired = false;
        await new OpenAIStreamProcessor().ParseOpenAiCompletionsAsync(
            stream,
            reader,
            model,
            api: "openai-completions",
            parseUsage: (_, usage, _) => usage,
            mapStopReason: _ => (StopReason.Stop, null),
            extractProviderErrorMessage: (raw, _) => raw,
            emitError: (_, _, _, _) => { },
            onMalformedChunk: () => callbackFired = true,
            ct: CancellationToken.None);

        await drain.WaitAsync(TimeSpan.FromSeconds(30));

        // The existing debug-log callback is preserved, not replaced - this adds a contract channel
        // alongside it rather than removing the log seam other code may depend on.
        callbackFired.ShouldBeTrue();

        var warning = captured.OfType<WarningEvent>().ShouldHaveSingleItem();
        warning.Code.ShouldBe(WarningCodes.MalformedChunkSkipped);
        // The skipped frame's bytes are untrusted and possibly content-bearing; they must not be
        // echoed into an event that reaches consumers and transcripts.
        warning.Message.ShouldNotContain("this is not json");

        // Non-terminal end to end: the good delta after the bad frame still lands.
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text)).ShouldBe("survived");
    }

    private static async Task<List<AssistantMessageEvent>> RunResponsesAsync(string sse)
    {
        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));

        var captured = new List<AssistantMessageEvent>();
        var drain = Task.Run(async () =>
        {
            await foreach (var evt in stream)
                captured.Add(evt);
        });

        await ResponsesStreamParser.ParseAsync(
            stream,
            reader,
            Model(),
            options: null,
            api: "openai-responses",
            logger: NullLogger.Instance,
            emitError: (_, _, _, _) => { },
            onParsedEvent: null,
            resolveConfiguredServiceTier: null,
            ct: CancellationToken.None);

        await drain.WaitAsync(TimeSpan.FromSeconds(30));
        return captured;
    }
}
