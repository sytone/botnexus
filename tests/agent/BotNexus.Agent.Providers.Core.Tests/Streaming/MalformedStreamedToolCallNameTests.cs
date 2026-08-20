using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Coverage for #3467: a streamed <c>tool_calls</c> delta may carry an index and argument
/// fragments before (or entirely without) a <c>function.name</c>. Both emission sites in
/// <see cref="OpenAIStreamProcessor"/> used to materialise such an accumulator as
/// <c>builder.Name ?? ""</c>, laundering an incomplete provider frame into a well-formed-looking
/// tool call that only failed at dispatch with the misdirecting text <c>Tool '' is not registered.</c>
/// These tests pin the drop at the stream boundary: no end event, no persisted content block, one
/// log record naming the index and id, and a stop reason derived from the surviving calls.
/// A well-formed call in the same stream must be untouched, which is what stops the guard from
/// being a blanket suppression.
/// </summary>
[Collection(BotNexus.Agent.Providers.Core.Tests.Diagnostics.ProviderDiagnosticsCollection.Name)]
public class MalformedStreamedToolCallNameTests : IDisposable
{
    private readonly RecordingLoggerFactory _factory = new();

    public MalformedStreamedToolCallNameTests()
    {
        ProviderDiagnostics.LoggerFactory = _factory;
    }

    public void Dispose()
    {
        ProviderDiagnostics.LoggerFactory = null!;
        GC.SuppressFinalize(this);
    }

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

    // ---- SSE builders ---------------------------------------------------

    /// <summary>A tool-call delta carrying an index and arguments but no function name.</summary>
    private static string NamelessChunk(int index, string? id, string argsFragment) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            id is null
                                ? (object)new { index, function = new { arguments = argsFragment } }
                                : new { index, id, function = new { arguments = argsFragment } }
                        }
                    }
                }
            }
        }) + "\n";

    private static string NamedChunk(int index, string id, string name, string argsFragment) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new { index, id, function = new { name, arguments = argsFragment } }
                        }
                    }
                }
            }
        }) + "\n";

    private static string FinishChunk(string finishReason) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { }, finish_reason = finishReason } }
        }) + "\n";

    // ---- drivers --------------------------------------------------------

    private static async Task<List<AssistantMessageEvent>> RunCompletionsAsync(string sse)
    {
        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        await new OpenAIStreamProcessor().ParseOpenAiCompletionsAsync(
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

    private static async Task<List<AssistantMessageEvent>> RunCompatAsync(string sse)
    {
        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        await new OpenAIStreamProcessor().ParseCompatAsync(
            stream,
            reader,
            Model,
            api: "openai-completions",
            parseUsage: (_, usage, _) => usage,
            mapStopReason: (reason, hasToolCalls) => hasToolCalls || reason == "tool_calls"
                ? (StopReason.ToolUse, null)
                : (StopReason.Stop, null),
            ct: CancellationToken.None);

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);
        return events;
    }

    private static AssistantMessage Final(List<AssistantMessageEvent> events) =>
        events.OfType<DoneEvent>().Single().Message;

    // ---- AC1 + AC2: the nameless call is dropped from events and content ----

    [Fact]
    public async Task Completions_ToolCallWithNoFunctionName_EmitsNoEndEventAndNoContentBlock()
    {
        var sse = NamelessChunk(0, "call_orphan", "{\"path\":")
                  + NamelessChunk(0, null, "\"/tmp/x\"}")
                  + FinishChunk("tool_calls")
                  + "data: [DONE]\n";

        var events = await RunCompletionsAsync(sse);

        events.OfType<ToolCallEndEvent>().ShouldBeEmpty();
        Final(events).Content.OfType<ToolCallContent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Compat_ToolCallWithNoFunctionName_EmitsNoEndEventAndNoContentBlock()
    {
        var sse = NamelessChunk(0, "call_orphan", "{\"path\":")
                  + NamelessChunk(0, null, "\"/tmp/x\"}")
                  + FinishChunk("tool_calls")
                  + "data: [DONE]\n";

        var events = await RunCompatAsync(sse);

        events.OfType<ToolCallEndEvent>().ShouldBeEmpty();
        Final(events).Content.OfType<ToolCallContent>().ShouldBeEmpty();
    }

    // ---- AC3: the drop is logged once, naming index and id ----------------

    [Fact]
    public async Task Completions_DroppedToolCall_IsLoggedOnceNamingIndexAndId()
    {
        var sse = NamelessChunk(3, "call_orphan", "{}")
                  + FinishChunk("tool_calls")
                  + "data: [DONE]\n";

        await RunCompletionsAsync(sse);

        var record = _factory.Records.ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.State["ToolCallIndex"].ShouldBe("3");
        record.State["ToolCallId"].ShouldBe("call_orphan");
    }

    [Fact]
    public async Task Compat_DroppedToolCall_IsLoggedOnceNamingIndexAndId()
    {
        var sse = NamelessChunk(2, "call_ghost", "{}")
                  + FinishChunk("tool_calls")
                  + "data: [DONE]\n";

        await RunCompatAsync(sse);

        var record = _factory.Records.ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.State["ToolCallIndex"].ShouldBe("2");
        record.State["ToolCallId"].ShouldBe("call_ghost");
    }

    // ---- AC4: stop reason is derived from the surviving calls -------------

    [Fact]
    public async Task Completions_AllToolCallsDropped_StopReasonIsNotToolUse()
    {
        var sse = NamelessChunk(0, "call_orphan", "{}")
                  + FinishChunk("tool_calls")
                  + "data: [DONE]\n";

        var events = await RunCompletionsAsync(sse);

        Final(events).StopReason.ShouldNotBe(StopReason.ToolUse);
        events.OfType<DoneEvent>().Single().Reason.ShouldNotBe(StopReason.ToolUse);
    }

    [Fact]
    public async Task Compat_AllToolCallsDropped_StopReasonIsNotToolUse()
    {
        // finish_reason is absent, so ToolUse could only come from the tool-call count.
        var sse = NamelessChunk(0, "call_orphan", "{}") + "data: [DONE]\n";

        var events = await RunCompatAsync(sse);

        Final(events).StopReason.ShouldNotBe(StopReason.ToolUse);
    }

    // ---- AC5: a well-formed call in the same stream is unaffected ---------

    [Fact]
    public async Task Completions_WellFormedCallSurvivesAlongsideDroppedOne()
    {
        var sse = NamedChunk(0, "call_good", "read_file", "{\"path\":\"/tmp/a\"}")
                  + NamelessChunk(1, "call_orphan", "{\"path\":\"/tmp/b\"}")
                  + FinishChunk("tool_calls")
                  + "data: [DONE]\n";

        var events = await RunCompletionsAsync(sse);

        var ended = events.OfType<ToolCallEndEvent>().ToList();
        ended.ShouldHaveSingleItem();
        ended[0].ToolCall.Name.ShouldBe("read_file");

        var calls = Final(events).Content.OfType<ToolCallContent>().ToList();
        calls.ShouldHaveSingleItem();
        calls[0].Name.ShouldBe("read_file");
        calls[0].Id.ShouldBe("call_good");
        calls[0].Arguments["path"].ShouldBe("/tmp/a");

        // A surviving tool call still reports ToolUse.
        Final(events).StopReason.ShouldBe(StopReason.ToolUse);
    }

    [Fact]
    public async Task Compat_WellFormedCallSurvivesAlongsideDroppedOne()
    {
        var sse = NamedChunk(0, "call_good", "read_file", "{\"path\":\"/tmp/a\"}")
                  + NamelessChunk(1, "call_orphan", "{\"path\":\"/tmp/b\"}")
                  + FinishChunk("tool_calls")
                  + "data: [DONE]\n";

        var events = await RunCompatAsync(sse);

        var ended = events.OfType<ToolCallEndEvent>().ToList();
        ended.ShouldHaveSingleItem();
        ended[0].ToolCall.Name.ShouldBe("read_file");

        var calls = Final(events).Content.OfType<ToolCallContent>().ToList();
        calls.ShouldHaveSingleItem();
        calls[0].Name.ShouldBe("read_file");
        calls[0].Id.ShouldBe("call_good");
        Final(events).StopReason.ShouldBe(StopReason.ToolUse);
    }

    /// <summary>
    /// The preserved text of a turn that also produced a malformed tool call must survive the
    /// content-block withdrawal — removing a block by index is only safe if it does not disturb
    /// the surrounding blocks.
    /// </summary>
    [Fact]
    public async Task Completions_TextContentSurvivesDroppedToolCall()
    {
        var text = "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { content = "thinking out loud" } } }
        }) + "\n";

        var sse = text
                  + NamelessChunk(0, "call_orphan", "{}")
                  + FinishChunk("tool_calls")
                  + "data: [DONE]\n";

        var events = await RunCompletionsAsync(sse);

        var final = Final(events);
        final.Content.OfType<TextContent>().Single().Text.ShouldBe("thinking out loud");
        final.Content.OfType<ToolCallContent>().ShouldBeEmpty();
    }

    // ---- recording logger ------------------------------------------------

    private sealed record LogRecord(LogLevel Level, string Message, IReadOnlyDictionary<string, string> State);

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<LogRecord> Records { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Records);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<LogRecord> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                        fields[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                }

                lock (records)
                    records.Add(new LogRecord(logLevel, formatter(state, exception), fields));
            }
        }
    }
}
