using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Regression pin for #3299: a <c>redacted_thinking</c> block must reach the assembled message as a
/// <see cref="ThinkingContent"/> carrying <c>Redacted == true</c>, and an ordinary <c>thinking</c>
/// block must NOT.
/// </summary>
/// <remarks>
/// The flag is load-bearing rather than decorative: <c>AnthropicMessageConverter</c> branches on
/// <c>thinking.Redacted == true</c> to re-emit the block as a wire-level <c>redacted_thinking</c>
/// with its opaque <c>data</c> payload. If the parser dropped the bit, a redacted block would be
/// replayed on the next turn as ordinary visible reasoning whose text is the literal placeholder
/// "[Reasoning redacted]" — silently corrupting the conversation Anthropic sees.
///
/// The behaviour was correct in the parser from the file's creation (d9fbf0a1) but had no test
/// anywhere in the repo, so nothing stopped a future edit collapsing the two switch arms. These
/// tests exist to make that collapse fail loudly. The discriminating assertion is deliberately
/// paired inside <see cref="Stream_RedactedAndOrdinaryThinking_FlagDiscriminatesBetweenThem"/>:
/// asserting only the redacted case would pass equally for a parser that flagged EVERY thinking
/// block, which would be a different and worse bug.
/// </remarks>
public class AnthropicRedactedThinkingTests
{
    private const string RedactedData = "EroBCkYIARgCKkBxq0";

    [Fact]
    public async Task Stream_RedactedThinkingBlock_SetsRedactedTrue()
    {
        var result = await RunAsync(BuildBody(redacted: true));

        var thinking = result.Content.OfType<ThinkingContent>().ShouldHaveSingleItem();

        thinking.Redacted.ShouldBe(true);
    }

    [Fact]
    public async Task Stream_OrdinaryThinkingBlock_DoesNotSetRedacted()
    {
        var result = await RunAsync(BuildBody(redacted: false));

        var thinking = result.Content.OfType<ThinkingContent>().ShouldHaveSingleItem();

        thinking.Redacted.ShouldNotBe(true);
    }

    [Fact]
    public async Task Stream_RedactedAndOrdinaryThinking_FlagDiscriminatesBetweenThem()
    {
        // Both block shapes in ONE stream. A parser that flagged every thinking block, or none,
        // fails here even though each single-shape test above could still be satisfied by one of
        // those two blanket behaviours.
        var result = await RunAsync(BuildTwoBlockBody());

        var thinking = result.Content.OfType<ThinkingContent>().ToList();
        thinking.Count.ShouldBe(2);

        // Index 0 is the ordinary block, index 1 the redacted one — emitted in that wire order.
        thinking[0].Redacted.ShouldNotBe(true);
        thinking[1].Redacted.ShouldBe(true);
    }

    [Fact]
    public async Task Stream_RedactedThinkingBlock_CarriesOpaqueDataAsSignature()
    {
        // The Redacted flag alone is not enough to round-trip: AnthropicMessageConverter emits the
        // signature as the wire "data" field, and drops the block entirely when it is blank. A fix
        // that set the flag but lost the payload would still corrupt replay.
        var result = await RunAsync(BuildBody(redacted: true));

        var thinking = result.Content.OfType<ThinkingContent>().ShouldHaveSingleItem();

        thinking.ThinkingSignature.ShouldBe(RedactedData);
        thinking.Thinking.ShouldBe("[Reasoning redacted]");
    }

    private static string BuildBody(bool redacted)
    {
        var body = new StringBuilder();
        AppendStart(body);
        AppendThinkingBlock(body, index: 0, redacted: redacted);
        AppendEnd(body);
        return body.ToString();
    }

    private static string BuildTwoBlockBody()
    {
        var body = new StringBuilder();
        AppendStart(body);
        AppendThinkingBlock(body, index: 0, redacted: false);
        AppendThinkingBlock(body, index: 1, redacted: true);
        AppendEnd(body);
        return body.ToString();
    }

    private static void AppendStart(StringBuilder body)
    {
        body.Append("event: message_start\n");
        body.Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_redact\",\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n");
    }

    private static void AppendThinkingBlock(StringBuilder body, int index, bool redacted)
    {
        body.Append("event: content_block_start\n");
        if (redacted)
        {
            // The redacted shape carries its opaque payload on the START frame — there are no
            // deltas — which is exactly why it needs its own switch arm in the parser.
            body.Append("data: {\"type\":\"content_block_start\",\"index\":");
            body.Append(index);
            body.Append(",\"content_block\":{\"type\":\"redacted_thinking\",\"data\":\"");
            body.Append(RedactedData);
            body.Append("\"}}\n\n");
        }
        else
        {
            body.Append("data: {\"type\":\"content_block_start\",\"index\":");
            body.Append(index);
            body.Append(",\"content_block\":{\"type\":\"thinking\"}}\n\n");

            body.Append("event: content_block_delta\n");
            body.Append("data: {\"type\":\"content_block_delta\",\"index\":");
            body.Append(index);
            body.Append(",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"visible reasoning\"}}\n\n");

            body.Append("event: content_block_delta\n");
            body.Append("data: {\"type\":\"content_block_delta\",\"index\":");
            body.Append(index);
            body.Append(",\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-visible\"}}\n\n");
        }

        body.Append("event: content_block_stop\n");
        body.Append("data: {\"type\":\"content_block_stop\",\"index\":");
        body.Append(index);
        body.Append("}\n\n");
    }

    private static void AppendEnd(StringBuilder body)
    {
        body.Append("event: message_delta\n");
        body.Append("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":9}}\n\n");
        body.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n");
    }

    private static async Task<AssistantMessage> RunAsync(string body)
    {
        var handler = new StreamingHandler(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(id: "claude-opus-5");
        var context = TestHelpers.MakeContext("redact");
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
