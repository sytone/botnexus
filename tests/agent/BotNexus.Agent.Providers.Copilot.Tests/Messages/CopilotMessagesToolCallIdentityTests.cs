using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Identity on streaming tool-call events for the Copilot Messages parser (#3290).
/// <para>
/// This parser is not part of <c>StreamingProviderConformanceTests</c>, where the shared #3290
/// assertion lives, so its contribution to the "every producer populates both fields" acceptance
/// criterion has to be pinned here or it is not pinned at all. A partial rollout in which one
/// producer emits a null id is worse than the status quo: a consumer that cannot rely on the field
/// being present has to keep the index-based guess as a fallback, and the guess is exactly what
/// #3290 removes.
/// </para>
/// <para>
/// Two <c>tool_use</c> blocks at distinct wire indices with interleaved <c>input_json_delta</c>
/// frames, because with one call in flight the old index-based resolution was trivially correct and
/// the test would prove nothing.
/// </para>
/// </summary>
public class CopilotMessagesToolCallIdentityTests
{
    [Fact]
    public async Task ProcessStreamAsync_InterleavedToolCalls_StartAndDeltaEventsCarryOwnIdentity()
    {
        var body =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_ident\"}}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_a\",\"name\":\"read_file\"}}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_b\",\"name\":\"write_file\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"/a\\\"}\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"/b\\\"}\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n";

        var events = await RunProviderAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)));

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        starts.Count.ShouldBe(2, "both tool_use blocks must open");
        starts.Select(s => s.ToolCallId).ShouldBe(new[] { "toolu_a", "toolu_b" }, ignoreOrder: true);
        starts.Select(s => s.ToolName).ShouldBe(new[] { "read_file", "write_file" }, ignoreOrder: true);

        var deltas = events.OfType<ToolCallDeltaEvent>().ToList();
        deltas.Count.ShouldBe(2, "each call contributed exactly one argument fragment");

        // Attribution, not mere presence: the fragment naming "/a" is toolu_a's and the one naming
        // "/b" is toolu_b's. Labelling both with the most recent call - the pre-#3290 fallback -
        // fails here while satisfying any presence-only check.
        var aDelta = deltas.Where(d => d.Delta.Contains("/a")).ShouldHaveSingleItem();
        aDelta.ToolCallId.ShouldBe("toolu_a");
        aDelta.ToolName.ShouldBe("read_file");

        var bDelta = deltas.Where(d => d.Delta.Contains("/b")).ShouldHaveSingleItem();
        bDelta.ToolCallId.ShouldBe("toolu_b");
        bDelta.ToolName.ShouldBe("write_file");
    }

    private static async Task<List<AssistantMessageEvent>> RunProviderAsync(Stream responseBody)
    {
        var handler = new StreamingHandler(responseBody);
        var provider = new CopilotMessagesProvider(new HttpClient(handler));
        var model = new LlmModel(
            Id: "claude-identity-test",
            Name: "claude-identity-test",
            Api: CopilotMessagesProvider.ApiId,
            Provider: "github-copilot",
            BaseUrl: "https://api.enterprise.githubcopilot.com",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 200000,
            MaxTokens: 16384);

        var context = new Context(
            SystemPrompt: "identity",
            Messages: [new UserMessage(new UserMessageContent("identity"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });

        var events = new List<AssistantMessageEvent>();
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var evt in stream.WithCancellation(readTimeout.Token))
            events.Add(evt);

        return events;
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
