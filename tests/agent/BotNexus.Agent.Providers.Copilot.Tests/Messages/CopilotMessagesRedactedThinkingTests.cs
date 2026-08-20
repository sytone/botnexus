using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Regression pin for #3299 on the Copilot Messages parser, which mirrors the Anthropic Messages
/// wire shape and therefore has the same <c>redacted_thinking</c> switch arm.
/// </summary>
/// <remarks>
/// #3299 asked whether the equivalent path exists here at all. It does:
/// <c>CopilotMessagesStreamParser.cs</c> has a <c>redacted_thinking</c> arm in both
/// <c>HandleContentBlockStart</c> and <c>HandleContentBlockStop</c>, and the stop arm already
/// constructs <c>new ThinkingContent(accumulated, signature, Redacted: true)</c>. So the clause is
/// satisfied by "already correct", and the gap this file closes is the absence of any test pinning
/// it — the same gap as on the Anthropic side.
///
/// The pairing rule applies here too: the ordinary-thinking assertion is what stops the flag being
/// a blanket true.
/// </remarks>
public class CopilotMessagesRedactedThinkingTests
{
    private const string RedactedData = "EroBCkYIARgCKkBxq0";

    [Fact]
    public async Task Stream_RedactedThinkingBlock_SetsRedactedTrue()
    {
        var result = await RunAsync(BuildBody(redacted: true));

        result.Content.OfType<ThinkingContent>().ShouldHaveSingleItem().Redacted.ShouldBe(true);
    }

    [Fact]
    public async Task Stream_OrdinaryThinkingBlock_DoesNotSetRedacted()
    {
        var result = await RunAsync(BuildBody(redacted: false));

        result.Content.OfType<ThinkingContent>().ShouldHaveSingleItem().Redacted.ShouldNotBe(true);
    }

    [Fact]
    public async Task Stream_RedactedAndOrdinaryThinking_FlagDiscriminatesBetweenThem()
    {
        var result = await RunAsync(BuildTwoBlockBody());

        var thinking = result.Content.OfType<ThinkingContent>().ToList();
        thinking.Count.ShouldBe(2);

        thinking[0].Redacted.ShouldNotBe(true);
        thinking[1].Redacted.ShouldBe(true);
    }

    [Fact]
    public async Task Stream_RedactedThinkingBlock_CarriesOpaqueDataAsSignature()
    {
        var thinking = (await RunAsync(BuildBody(redacted: true)))
            .Content.OfType<ThinkingContent>().ShouldHaveSingleItem();

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
        var provider = new CopilotMessagesProvider(new HttpClient(handler));
        var model = new LlmModel(
            Id: "claude-opus-5",
            Name: "claude-opus-5",
            Api: CopilotMessagesProvider.ApiId,
            Provider: "github-copilot",
            BaseUrl: "https://api.enterprise.githubcopilot.com",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 200000,
            MaxTokens: 16384);
        var context = new Context(
            SystemPrompt: "redact",
            Messages: [new UserMessage(new UserMessageContent("redact"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
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
