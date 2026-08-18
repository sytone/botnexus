using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Regression coverage for #3336: the Copilot Messages stream parser must reconcile the text it
/// assembled from deltas against the provider's own final block text, as
/// <c>ResponsesStreamParser</c> has since #2443.
/// </summary>
/// <remarks>
/// Copilot model discovery routes a model to whichever transport the account exposes, so a fix
/// applied to Responses alone is one discovery decision away from a recurrence - the recorded
/// history of #2049 -> #2170. The model id used here is a <c>claude-*</c> one on purpose: it is
/// outside the <c>gpt-5.6</c> prefix that used to decide whether the lossy strip applied, so these
/// tests can only pass through reconciliation or through the new transport-declared flag.
/// </remarks>
public class CopilotMessagesStreamAssemblyReconciliationTests
{
    private const string Canonical = "Line one\nLine two with a URL https://example.com/a_b and `code`.";

    [Fact]
    public async Task ContentBlockStop_WithProviderFinalText_PrefersItOverCrlfFramedDeltas()
    {
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
        // The leading-prefix strip cannot reach an interior artifact. Only preferring the
        // provider's canonical value repairs this, so the test isolates reconciliation from the
        // normalizer.
        var result = await RunAsync(BuildBody(
            deltas: ["Line\r\n one\n", "Line two with a URL https://example.com/a_b and `code`."],
            finalText: Canonical));

        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe(Canonical);
    }

    [Fact]
    public async Task ContentBlockStop_WithNoProviderFinalText_LeavesAssembledTextUntouched()
    {
        var result = await RunAsync(BuildBody(deltas: ["alpha", " beta"], finalText: null));

        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("alpha beta");
    }

    [Fact]
    public async Task ContentBlockStop_WhenAssembledMatchesFinal_IsAByteIdenticalPassthrough()
    {
        // #3336 AC3: intentional Markdown structure round-trips verbatim.
        const string markdown =
            "Intro paragraph.\n\nSecond paragraph with https://example.com/x?y=1&z=2\n\n" +
            "- item one\n- item two\n\n| a | b |\n| - | - |\n| 1 | 2 |\n\n```csharp\nvar x = 1;\n```\n";

        var result = await RunAsync(BuildBody(
            deltas: [markdown[..20], markdown[20..]],
            finalText: markdown));

        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe(markdown);
    }

    [Fact]
    public async Task CrlfFramedDeltas_OnANonGpt56Model_AreNormalizedByTheTransportFlagAlone()
    {
        // No provider final text here, so reconciliation has nothing to check against: this pins
        // the OTHER half of the fix, the transport-declared strip replacing the gpt-5.6 model-id
        // prefix gate. Under the old gate a claude-* id skipped normalization entirely and the
        // CRLFs reached content.
        var result = await RunAsync(BuildBody(
            deltas: ["\r\nalpha", "\r\n beta"],
            finalText: null));

        var text = result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text;

        text.ShouldBe("alpha beta");
        text.ShouldNotContain("\r");
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
        var provider = new CopilotMessagesProvider(new HttpClient(handler));
        var model = new LlmModel(
            // Deliberately NOT a gpt-5.6 id.
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
            SystemPrompt: "reconcile",
            Messages: [new UserMessage(new UserMessageContent("reconcile"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
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
