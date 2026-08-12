using System.Text;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Parser-level proof that the Responses stream is reconciled against the provider's own
/// <c>response.output_text.done.text</c> before the block closes (#2443).
/// </summary>
/// <remarks>
/// Unit tests on <see cref="StreamAssemblyConformance"/> prove the comparison is correct; they
/// cannot prove it is wired. These drive real SSE bytes through the parser, which is the only thing
/// that can detect the check being removed from the event loop.
/// </remarks>
public class ResponsesStreamAssemblyConformanceTests
{
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

    private static string Sse(IEnumerable<string> deltas, string? doneText)
    {
        var builder = new StringBuilder();
        builder.Append("event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n");

        foreach (var delta in deltas)
        {
            builder.Append("event: response.output_text.delta\ndata: {\"item_id\":\"item_1\",\"delta\":");
            builder.Append(System.Text.Json.JsonSerializer.Serialize(delta));
            builder.Append("}\n\n");
        }

        if (doneText is not null)
        {
            builder.Append("event: response.output_text.done\ndata: {\"item_id\":\"item_1\",\"text\":");
            builder.Append(System.Text.Json.JsonSerializer.Serialize(doneText));
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

    private static async Task<string> RunAsync(
        IEnumerable<string> deltas,
        string? doneText,
        Func<LlmModel, string, string>? normalize = null)
    {
        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(Sse(deltas, doneText))));

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
            normalizeTextDelta: normalize,
            ct: CancellationToken.None);

        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));
    }

    // Happy path: a clean stream is byte-identical to the provider's final text and must be
    // untouched. AC6 - no behaviour change for clean streams.
    [Fact]
    public async Task CleanStream_MatchingFinalText_IsUnchanged()
        => (await RunAsync(["Hello", " ", "world"], "Hello world")).ShouldBe("Hello world");

    // Mid-word splits are transport metadata and must survive assembly untouched.
    [Fact]
    public async Task MidWordSplitDeltas_ReassembleExactly()
        => (await RunAsync(["stre", "aming"], "streaming")).ShouldBe("streaming");

    // A newline-only delta is legitimate model content (Markdown structure) and must be preserved.
    [Fact]
    public async Task NewlineOnlyDelta_IsPreserved()
        => (await RunAsync(["para one", "\n\n", "para two"], "para one\n\npara two"))
            .ShouldBe("para one\n\npara two");

    // Sad path: the assembled buffer carries a transport artifact the provider's own final text
    // does not. This is the #2049/#2119/#2170 shape, and the provider's value must win.
    [Fact]
    public async Task AssembledTextDivergesFromFinalText_ProviderFinalTextWins()
        => (await RunAsync(["Hello", "\r\n world"], "Hello world")).ShouldBe("Hello world");

    // A normalizer that over-strips is itself an assembly defect. The conformance check must catch
    // its own transport hook eating legitimate content, otherwise it only guards other people's bugs.
    [Fact]
    public async Task OverAggressiveNormalizerDeletesContent_ConformanceRestoresIt()
    {
        var assembled = await RunAsync(
            ["Hello", "\r\nworld"],
            "Hello\r\nworld",
            normalize: static (_, delta) => delta.StartsWith("\r\n", StringComparison.Ordinal) ? delta[2..] : delta);

        assembled.ShouldBe("Hello\r\nworld");
    }

    // No final text on the wire is not a mismatch - it means there is nothing to check against,
    // and the stream must still assemble normally rather than being blanked.
    [Fact]
    public async Task NoDoneEvent_StreamStillAssemblesFromDeltas()
        => (await RunAsync(["Hello", " world"], doneText: null)).ShouldBe("Hello world");

    // A done event for an item we never saw a delta for must not invent a text block.
    [Fact]
    public async Task DoneEventForUnknownItem_IsIgnored()
    {
        var sse =
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n" +
            "event: response.output_text.done\ndata: {\"item_id\":\"ghost\",\"text\":\"phantom\"}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n";

        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));

        await ResponsesStreamParser.ParseAsync(
            stream, reader, Model(), options: null, api: "openai-responses",
            logger: NullLogger.Instance, emitError: (_, _, _, _) => { }, onParsedEvent: null,
            resolveConfiguredServiceTier: null, normalizeTextDelta: null, ct: CancellationToken.None);

        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.Content.OfType<TextContent>().ShouldBeEmpty();
    }
}
