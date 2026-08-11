using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Guards the Copilot Messages stream parser's tool-call argument accumulation against unbounded
/// growth (issue #2902). <c>input_json_delta</c> fragments were previously appended to a
/// <see cref="StringBuilder"/> with no cumulative byte accounting. These tests assert that an
/// over-budget tool call is terminated with a distinguishable error and that a normal
/// multi-fragment tool call still parses exactly as before.
/// </summary>
public class CopilotMessagesToolArgumentBudgetTests
{
    // The parser's per-SSE-frame body cap. Fragments must stay under it so these tests exercise the
    // tool-argument budget rather than the body guard.
    private const int FrameSafeFragmentBytes = 32 * 1024;

    [Fact]
    public async Task ProcessStreamAsync_ToolArgumentsOverBudget_TerminatesWithError()
    {
        StreamToolArgumentBudget.ResetConfiguredMaxBytes();
        var body = new StringBuilder();
        body.Append("event: message_start\n");
        body.Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_budget\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}\n\n");
        body.Append("event: content_block_start\n");
        body.Append("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"write_file\"}}\n\n");
        var fragment = new string('a', FrameSafeFragmentBytes);
        for (var i = 0; i < 40; i++) // ~1.25 MiB > the default 1 MiB budget
        {
            body.Append("event: content_block_delta\n");
            body.Append("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"");
            body.Append(fragment);
            body.Append("\"}}\n\n");
        }
        body.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n");

        var result = await RunProviderAsync(new MemoryStream(Encoding.UTF8.GetBytes(body.ToString())));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("limit", Case.Insensitive);
        result.ErrorMessage!.ShouldContain("write_file");
        // No truncated-and-therefore-invalid tool call was emitted as if it were complete.
        result.Content.OfType<ToolCallContent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task ProcessStreamAsync_NormalMultiFragmentToolCall_ParsesUnchanged()
    {
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

        var result = await RunProviderAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)));

        result.StopReason.ShouldNotBe(StopReason.Error);
        result.ErrorMessage.ShouldBeNull();
        var toolCall = result.Content.OfType<ToolCallContent>().ShouldHaveSingleItem();
        toolCall.Id.ShouldBe("toolu_1");
        toolCall.Name.ShouldBe("read_file");
        toolCall.Arguments["path"].ShouldBe("/tmp/x");
    }

    private static async Task<AssistantMessage> RunProviderAsync(Stream responseBody)
    {
        var handler = new StreamingHandler(responseBody);
        var provider = new CopilotMessagesProvider(new HttpClient(handler));
        var model = new LlmModel(
            Id: "claude-budget-test",
            Name: "claude-budget-test",
            Api: CopilotMessagesProvider.ApiId,
            Provider: "github-copilot",
            BaseUrl: "https://api.enterprise.githubcopilot.com",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 200000,
            MaxTokens: 16384);
        var context = new Context(
            SystemPrompt: "budget",
            Messages: [new UserMessage(new UserMessageContent("budget"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
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
