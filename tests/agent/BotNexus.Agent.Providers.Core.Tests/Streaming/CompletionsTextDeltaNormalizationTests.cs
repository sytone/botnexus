using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Pins that the Chat Completions path honours the same text-delta normalization hook as the
/// Responses and Messages paths (#2443).
/// </summary>
/// <remarks>
/// Completions was the one Copilot transport with no normalization. That asymmetry is the exact
/// mechanism by which #2170 reproduced #2049: the fix lived on one transport, discovery selected a
/// different one, and the artifact returned. These tests are what makes "exactly one implementation,
/// applied by all transports" a checked property of the engine rather than a claim in a doc comment.
/// </remarks>
public class CompletionsTextDeltaNormalizationTests
{
    private static readonly LlmModel Model = new(
        Id: "gpt-5.6",
        Name: "GPT-5.6",
        Api: "github-copilot-completions",
        Provider: "github-copilot",
        BaseUrl: "https://api.enterprise.githubcopilot.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 16384);

    private static string ContentChunk(string text) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { content = text } } }
        }) + "\n";

    private static async Task<string> RunAsync(
        IEnumerable<string> deltas,
        Func<LlmModel, string, string>? normalize)
    {
        var sse = string.Concat(deltas.Select(ContentChunk)) + "data: [DONE]\n";

        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        var processor = new OpenAIStreamProcessor();

        await processor.ParseOpenAiCompletionsAsync(
            stream,
            reader,
            Model,
            api: "github-copilot-completions",
            parseUsage: (_, usage, _) => usage,
            mapStopReason: _ => (StopReason.Stop, null),
            extractProviderErrorMessage: (raw, _) => raw,
            emitError: (_, _, _, _) => { },
            onMalformedChunk: null,
            ct: CancellationToken.None,
            inspectChunk: null,
            normalizeTextDelta: normalize);

        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));
    }

    // Sad path first: without a hook, the artifact reaches the accumulated content. This is the
    // pre-fix behaviour of the Completions transport, asserted so the fix has something to reverse.
    [Fact]
    public async Task NoNormalizer_LeavesDeltasByteIdentical()
        => (await RunAsync(["\r\nHello", " world"], normalize: null)).ShouldBe("\r\nHello world");

    // Fire: the hook is applied to every text delta, not just the first.
    [Fact]
    public async Task Normalizer_IsAppliedToEveryTextDelta()
    {
        var assembled = await RunAsync(
            ["\r\nHello", "\r\n world"],
            static (_, delta) => delta.StartsWith("\r\n", StringComparison.Ordinal) ? delta[2..] : delta);

        assembled.ShouldBe("Hello world");
    }

    // A delta that normalizes to empty must not open a text block or emit an empty delta event.
    [Fact]
    public async Task DeltaThatNormalizesToEmpty_ProducesNoTextContent()
    {
        var assembled = await RunAsync(
            ["\r\n"],
            static (_, delta) => delta.Replace("\r\n", "", StringComparison.Ordinal));

        assembled.ShouldBe("");
    }

    // Clean content must be untouched by the presence of a hook (AC6).
    [Fact]
    public async Task Normalizer_LeavesCleanContentAndBareLfIntact()
    {
        var assembled = await RunAsync(
            ["para one", "\n\n", "para two"],
            static (_, delta) => delta.StartsWith("\r\n", StringComparison.Ordinal) ? delta[2..] : delta);

        assembled.ShouldBe("para one\n\npara two");
    }
}
