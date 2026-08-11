using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Guards the Anthropic stream parser's tool-call argument accumulation against unbounded growth
/// (issue #2902). <c>input_json_delta</c> fragments were previously appended to a
/// <see cref="StringBuilder"/> with no cumulative byte accounting, so a hostile or malfunctioning
/// Anthropic-compatible endpoint could grow the heap without limit. These tests assert that an
/// over-budget tool call is terminated with a distinguishable error, and that a normal
/// multi-fragment tool call still parses exactly as before.
/// </summary>
public class AnthropicToolArgumentBudgetTests
{
    [Fact]
    public async Task Stream_ToolArgumentsOverBudget_TerminatesWithError()
    {
        // Drive ~1.25 MiB of input_json_delta fragments through one tool_use block, past the
        // default 1 MiB budget. Each SSE frame stays well under the 8 MiB per-frame body cap, so
        // this exercises the tool-argument budget specifically and not the body guard.
        StreamToolArgumentBudget.ResetConfiguredMaxBytes();
        var body = new StringBuilder();
        body.Append("event: message_start\n");
        body.Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_budget\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}\n\n");
        body.Append("event: content_block_start\n");
        body.Append("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"write_file\"}}\n\n");
        var fragment = new string('a', 32 * 1024);
        for (var i = 0; i < 40; i++)
        {
            body.Append("event: content_block_delta\n");
            body.Append("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"");
            body.Append(fragment);
            body.Append("\"}}\n\n");
        }
        body.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n");

        var result = await RunProviderAsync(HttpStatusCode.OK, new MemoryStream(Encoding.UTF8.GetBytes(body.ToString())));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        // Distinguishable: the message names the tool call and the byte limit, not a generic
        // transport failure, and no tool call was emitted as if it were complete.
        result.ErrorMessage!.ShouldContain("limit", Case.Insensitive);
        result.ErrorMessage!.ShouldContain("write_file");
        result.Content.OfType<ToolCallContent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Stream_NormalMultiFragmentToolCall_ParsesUnchanged()
    {
        // Parity guard: under the cap, a multi-fragment tool call must produce the same id, name
        // and parsed arguments it did before the budget existed.
        StreamToolArgumentBudget.ResetConfiguredMaxBytes();
        var body =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_ok\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"read_file\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\": \"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"/tmp/x\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"}\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":3}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n";

        var result = await RunProviderAsync(HttpStatusCode.OK, new MemoryStream(Encoding.UTF8.GetBytes(body)));

        result.StopReason.ShouldNotBe(StopReason.Error);
        result.ErrorMessage.ShouldBeNull();
        var toolCall = result.Content.OfType<ToolCallContent>().ShouldHaveSingleItem();
        toolCall.Id.ShouldBe("toolu_1");
        toolCall.Name.ShouldBe("read_file");
        toolCall.Arguments["path"].ShouldBe("/tmp/x");
    }

    private static async Task<AssistantMessage> RunProviderAsync(HttpStatusCode status, Stream body)
    {
        var handler = new StreamingHandler(status, body);
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(id: "claude-budget-test");
        var context = TestHelpers.MakeContext("budget");
        var stream = provider.Stream(model, context, new SimpleStreamOptions { ApiKey = "test-key" });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class StreamingHandler(HttpStatusCode status, Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
