using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Regression coverage for #3336: the Anthropic stream parser must reconcile the text it assembled
/// from deltas against the provider's own final block text, exactly as
/// <c>ResponsesStreamParser</c> has since #2443.
/// </summary>
/// <remarks>
/// The reconciliation was wired into ONE of the three parsers, so an Anthropic-shaped stream had no
/// protection at all: a per-delta transport artifact (CRLF framing, observed on <c>claude-opus-5</c>
/// over the relay paths) accumulated verbatim into <c>session_history</c>. The model-id prefix gate
/// on the Copilot normalizer meant no Anthropic model was covered by the lossy strip either. These
/// tests use a plain <c>claude-*</c> model id specifically because it is outside every model-family
/// gate that previously existed.
/// </remarks>
public class AnthropicStreamAssemblyReconciliationTests
{
    private const string Canonical = "Line one\nLine two with a URL https://example.com/a_b and `code`.";

    [Fact]
    public async Task ContentBlockStop_WithProviderFinalText_PrefersItOverCrlfFramedDeltas()
    {
        // Every delta arrives CRLF-prefixed - the exact wire shape reported in #3336 - and the stop
        // frame carries the provider's own final text.
        var result = await RunAsync(BuildBody(
            deltas: ["\r\nLine one", "\r\n\nLine two with a URL ", "\r\nhttps://example.com/a_b and `code`."],
            finalText: Canonical));

        var text = result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text;

        text.ShouldBe(Canonical);
        text.ShouldNotContain("\r\n");
        text.ShouldNotContain("\r");
    }

    [Fact]
    public async Task ContentBlockStop_WithProviderFinalText_AlsoRepairsInteriorArtifacts()
    {
        // A prefix strip could never fix this: the artifact is INSIDE the delta. Only preferring
        // the provider's canonical value repairs it, so this test can only pass via reconciliation.
        var result = await RunAsync(BuildBody(
            deltas: ["Line\r\n one\n", "Line two with a URL https://example.com/a_b and `code`."],
            finalText: Canonical));

        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe(Canonical);
    }

    [Fact]
    public async Task ContentBlockStop_WithNoProviderFinalText_LeavesAssembledTextUntouched()
    {
        // Fail-open contract: the Messages spec does not require a final text on the stop frame, so
        // absence must mean "nothing to check against", never "replace with empty".
        var result = await RunAsync(BuildBody(deltas: ["alpha", " beta"], finalText: null));

        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("alpha beta");
    }

    [Fact]
    public async Task ContentBlockStop_WhenAssembledMatchesFinal_IsAByteIdenticalPassthrough()
    {
        // Positive pin: reconciliation must not perturb a clean stream. Intentional Markdown
        // structure - bare-LF paragraph breaks, a fenced block, a list, a table and a URL - has to
        // round-trip verbatim (#3336 AC3).
        const string markdown =
            "Intro paragraph.\n\nSecond paragraph with https://example.com/x?y=1&z=2\n\n" +
            "- item one\n- item two\n\n| a | b |\n| - | - |\n| 1 | 2 |\n\n```csharp\nvar x = 1;\n```\n";

        var result = await RunAsync(BuildBody(
            deltas: [markdown[..20], markdown[20..]],
            finalText: markdown));

        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe(markdown);
    }

    private static string BuildBody(string[] deltas, string? finalText)
    {
        var body = new StringBuilder();
        body.Append("event: message_start\n");
        body.Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_reconcile\",\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n");
        body.Append("event: content_block_start\n");
        body.Append("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n\n");

        foreach (var delta in deltas)
        {
            body.Append("event: content_block_delta\n");
            body.Append("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":");
            body.Append(System.Text.Json.JsonSerializer.Serialize(delta));
            body.Append("}}\n\n");
        }

        body.Append("event: content_block_stop\n");
        body.Append("data: {\"type\":\"content_block_stop\",\"index\":0");
        if (finalText is not null)
        {
            body.Append(",\"text\":");
            body.Append(System.Text.Json.JsonSerializer.Serialize(finalText));
        }
        body.Append("}\n\n");

        body.Append("event: message_delta\n");
        body.Append("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":9}}\n\n");
        body.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n");
        return body.ToString();
    }

    private static async Task<AssistantMessage> RunAsync(string body)
    {
        var handler = new StreamingHandler(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        var provider = new AnthropicProvider(new HttpClient(handler));
        // Deliberately NOT a gpt-5.6 id: the old normalizer gate would have skipped this entirely.
        var model = TestHelpers.MakeModel(id: "claude-opus-5");
        var context = TestHelpers.MakeContext("reconcile");
        var stream = provider.Stream(model, context, new SimpleStreamOptions { ApiKey = "test-key" });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class StreamingHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
