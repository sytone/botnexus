using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Guards streamed tool-call argument accumulation against unbounded growth (issue #2902).
/// Before this guard every <c>arguments</c> fragment was appended to a <see cref="StringBuilder"/>
/// with no cumulative accounting, so a malicious or malfunctioning provider stream could grow the
/// heap without limit - and then double it again on the <c>ToString()</c> each incremental parse
/// performs. These tests pin three things: the budget is measured in UTF-8 bytes (not UTF-16
/// chars), an over-budget stream is rejected with a distinguishable error rather than a truncated
/// argument blob, and an under-budget multi-fragment tool call parses exactly as it did before.
/// </summary>
public class StreamToolArgumentBudgetTests
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

    // ----------------------------------------------------------------------------------------
    // Direct unit tests for the reusable budget.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Append_UnderBudget_AppendsFragmentVerbatim()
    {
        var budget = new StreamToolArgumentBudget(1024, "openai", "gpt-4o", "tool 'read'");
        var sb = new StringBuilder();

        budget.Append(sb, "{\"path\":");
        budget.Append(sb, "\"/tmp/x\"}");

        sb.ToString().ShouldBe("{\"path\":\"/tmp/x\"}");
        budget.ObservedBytes.ShouldBe(17);
    }

    [Fact]
    public void Append_MeasuresUtf8BytesNotChars()
    {
        // Each 'é' is ONE UTF-16 char but TWO UTF-8 bytes. A char-based counter would see 6 and
        // stay under a 10-byte budget; a correct byte-based counter sees 12 and rejects. This is
        // acceptance criterion 2: multi-byte payloads must not exceed the intended memory ceiling.
        var budget = new StreamToolArgumentBudget(10, "openai", "gpt-4o", "tool 'read'");
        var sb = new StringBuilder();
        var sixCharsTwelveBytes = new string('é', 6);

        Should.Throw<StreamToolArgumentsTooLargeException>(() => budget.Append(sb, sixCharsTwelveBytes));
        budget.ObservedBytes.ShouldBe(12);
        // Nothing was appended: an over-budget fragment must never land in the buffer.
        sb.Length.ShouldBe(0);
    }

    [Fact]
    public void Append_FourByteAstralCharacters_CountedAsFourBytes()
    {
        // U+1F600 is 2 UTF-16 chars but 4 UTF-8 bytes - the worst case for a char-based counter.
        var budget = new StreamToolArgumentBudget(1024, "openai", "gpt-4o", "tool 'read'");
        var sb = new StringBuilder();

        budget.Append(sb, "\U0001F600");

        budget.ObservedBytes.ShouldBe(4);
        sb.Length.ShouldBe(2);
    }

    [Fact]
    public void Append_OverBudget_ReportsBudgetProviderAndModel()
    {
        var budget = new StreamToolArgumentBudget(8, "anthropic", "claude-x", "tool 'edit' (block 0)");
        var sb = new StringBuilder();

        var ex = Should.Throw<StreamToolArgumentsTooLargeException>(() => budget.Append(sb, new string('a', 9)));

        ex.MaxBytes.ShouldBe(8);
        ex.ObservedBytes.ShouldBe(9);
        ex.Provider.ShouldBe("anthropic");
        ex.ModelId.ShouldBe("claude-x");
        ex.Description.ShouldBe("tool 'edit' (block 0)");
    }

    [Fact]
    public void Append_ExactlyAtBudget_IsAccepted()
    {
        // The budget is a ceiling, not a strict-less-than: a payload of exactly MaxBytes is legal.
        var budget = new StreamToolArgumentBudget(8, "openai", "gpt-4o", "tool 'read'");
        var sb = new StringBuilder();

        budget.Append(sb, new string('a', 8));

        sb.Length.ShouldBe(8);
        budget.ObservedBytes.ShouldBe(8);
    }

    [Fact]
    public void ConfiguredMaxBytes_DefaultsToDefaultMaxBytes_AndRejectsNonPositiveOverride()
    {
        StreamToolArgumentBudget.ResetConfiguredMaxBytes();
        try
        {
            StreamToolArgumentBudget.ConfiguredMaxBytes.ShouldBe(StreamToolArgumentBudget.DefaultMaxBytes);

            StreamToolArgumentBudget.ConfiguredMaxBytes = 4096;
            StreamToolArgumentBudget.ConfiguredMaxBytes.ShouldBe(4096);
            StreamToolArgumentBudget.ForToolCall("p", "m", "d").MaxBytes.ShouldBe(4096);

            // A non-positive assignment must NOT disable the guard - it restores the default.
            StreamToolArgumentBudget.ConfiguredMaxBytes = 0;
            StreamToolArgumentBudget.ConfiguredMaxBytes.ShouldBe(StreamToolArgumentBudget.DefaultMaxBytes);
        }
        finally
        {
            StreamToolArgumentBudget.ResetConfiguredMaxBytes();
        }
    }

    [Fact]
    public void Constructor_RejectsNonPositiveBudget()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new StreamToolArgumentBudget(0, "p", "m", "d"));
    }

    // ----------------------------------------------------------------------------------------
    // End-to-end through OpenAIStreamProcessor.ParseOpenAiCompletionsAsync.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ParseOpenAiCompletions_ToolArgumentsOverBudget_TerminatesWithDistinguishableError()
    {
        // A stream of argument fragments whose cumulative size crosses the default 1 MiB budget.
        // The processor must abort with StreamToolArgumentsTooLargeException rather than emit a
        // truncated-and-therefore-invalid argument blob as if it were complete.
        var sse = new StringBuilder();
        sse.Append(ToolCallStartChunk("call_overflow", "write_file"));
        var fragment = new string('a', 32 * 1024); // 32 KiB per delta, under the SSE frame cap
        for (var i = 0; i < 40; i++) // 1.25 MiB total > 1 MiB budget
            sse.Append(DeltaChunk(fragment));

        var ex = await Should.ThrowAsync<StreamToolArgumentsTooLargeException>(
            () => RunCompletionsAsync(sse.ToString()));

        ex.MaxBytes.ShouldBe(StreamToolArgumentBudget.DefaultMaxBytes);
        ex.ObservedBytes.ShouldBeGreaterThan(StreamToolArgumentBudget.DefaultMaxBytes);
        ex.Provider.ShouldBe("openai");
        ex.ModelId.ShouldBe("gpt-4o");
        ex.Description.ShouldContain("write_file");
    }

    [Fact]
    public async Task ParseOpenAiCompletions_NormalMultiFragmentToolCall_ParsesUnchanged()
    {
        // Parity guard: an ordinary multi-fragment tool call must produce exactly the same final
        // tool call it did before the budget existed - same id, name, and parsed arguments.
        var sse = ToolCallStartChunk("call_1", "read_file")
            + DeltaChunk("{\"path\": ")
            + DeltaChunk("\"/tmp/x")
            + DeltaChunk("\"}");

        var events = await RunCompletionsAsync(sse);

        var end = events.OfType<ToolCallEndEvent>().ShouldHaveSingleItem();
        end.ToolCall.Id.ShouldBe("call_1");
        end.ToolCall.Name.ShouldBe("read_file");
        end.ToolCall.Arguments["path"].ShouldBe("/tmp/x");
    }

    [Fact]
    public async Task ParseCompat_ToolArgumentsOverBudget_TerminatesWithDistinguishableError()
    {
        // The compat path has its own accumulation site (ToolCallBuilder.ArgumentsJson) and must be
        // bounded too, otherwise the guard has a hole for every OpenAI-compatible provider.
        var sse = new StringBuilder();
        sse.Append(ToolCallStartChunk("call_overflow", "write_file"));
        var fragment = new string('a', 32 * 1024);
        for (var i = 0; i < 40; i++)
            sse.Append(DeltaChunk(fragment));

        var ex = await Should.ThrowAsync<StreamToolArgumentsTooLargeException>(
            () => RunCompatAsync(sse.ToString()));

        ex.MaxBytes.ShouldBe(StreamToolArgumentBudget.DefaultMaxBytes);
        ex.ObservedBytes.ShouldBeGreaterThan(StreamToolArgumentBudget.DefaultMaxBytes);
        ex.Description.ShouldContain("write_file");
    }

    [Fact]
    public async Task ParseCompat_NormalMultiFragmentToolCall_ParsesUnchanged()
    {
        var sse = ToolCallStartChunk("call_1", "read_file")
            + DeltaChunk("{\"path\": ")
            + DeltaChunk("\"/tmp/x")
            + DeltaChunk("\"}");

        var events = await RunCompatAsync(sse);

        var end = events.OfType<ToolCallEndEvent>().ShouldHaveSingleItem();
        end.ToolCall.Id.ShouldBe("call_1");
        end.ToolCall.Name.ShouldBe("read_file");
        end.ToolCall.Arguments["path"].ShouldBe("/tmp/x");
    }

    // ----------------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------------

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

    private static async Task<List<AssistantMessageEvent>> RunCompletionsAsync(string sse)
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

        return await DrainAsync(stream);
    }

    private static async Task<List<AssistantMessageEvent>> RunCompatAsync(string sse)
    {
        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        var processor = new OpenAIStreamProcessor();
        await processor.ParseCompatAsync(
            stream,
            reader,
            Model,
            api: "openai-compat",
            parseUsage: (_, usage, _) => usage,
            mapStopReason: (reason, hasToolCalls) => hasToolCalls || reason == "tool_calls"
                ? (StopReason.ToolUse, null)
                : (StopReason.Stop, null),
            ct: CancellationToken.None);

        return await DrainAsync(stream);
    }

    private static async Task<List<AssistantMessageEvent>> DrainAsync(LlmStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);
        return events;
    }
}
