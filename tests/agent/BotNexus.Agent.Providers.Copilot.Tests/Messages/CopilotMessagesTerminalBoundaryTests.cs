using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Pins the provider's terminal contract: transport EOF is not a Messages message_stop (#4621).
/// Short prose and tool calls remain valid when the wire explicitly completes the message.
/// </summary>
public sealed class CopilotMessagesTerminalBoundaryTests
{
    private const string PartialText = "PR/Migration private-content-sentinel-4621";
    private const string IncompleteDiagnostic = "Copilot streaming error: stream ended before message_stop.";

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, true, "end_turn")]
    [InlineData(true, true, "tool_use")]
    [InlineData(true, true, "max_tokens")]
    public async Task Stream_EofWithoutMessageStop_EmitsErrorAndPreservesPartialText(
        bool includeText, bool closeBlock, string? wireStopReason)
    {
        var body = MessageStart() + TextStart();
        if (includeText)
            body += TextDelta(PartialText);
        if (closeBlock)
            body += BlockStop(0);
        if (wireStopReason is not null)
            body += MessageDelta(wireStopReason);

        var (events, result) = await RunProviderAsync(body);

        // Require the typed terminal event, not merely an error-shaped final message.
        events.OfType<DoneEvent>().ShouldBeEmpty();
        var error = events.OfType<ErrorEvent>().ShouldHaveSingleItem();
        events.Last().ShouldBeSameAs(error);
        error.Reason.ShouldBe(StopReason.Error);
        error.Error.ShouldBeSameAs(result);
        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldBe(IncompleteDiagnostic);
        result.ResponseId.ShouldBe("msg_terminal_4621");

        var expectedText = includeText ? PartialText : string.Empty;
        if (includeText)
            result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe(expectedText);
        else
            result.Content.ShouldBeEmpty();
        string.Concat(events.OfType<TextDeltaEvent>().Select(e => e.Delta)).ShouldBe(expectedText);
        events.OfType<TextEndEvent>().Count().ShouldBe(closeBlock ? 1 : 0);

        var diagnostic = result.ErrorMessage;
        diagnostic.ShouldNotBeNull();
        diagnostic.ShouldNotContain(PartialText);
        diagnostic.ShouldNotContain("private-content-sentinel-4621");
    }

    [Theory]
    [InlineData("PR", "end_turn", StopReason.Stop)]
    [InlineData("Migration", "end_turn", StopReason.Stop)]
    [InlineData("Short answer", "max_tokens", StopReason.Length)]
    public async Task Stream_ShortTextWithMessageStop_PreservesTextAndProviderStopReason(
        string text, string wireStopReason, StopReason expectedReason)
    {
        var body = MessageStart() + TextStart() + TextDelta(text) + BlockStop(0)
            + MessageDelta(wireStopReason) + MessageStop();

        var (events, result) = await RunProviderAsync(body);

        AssertCompleted(events, result, expectedReason);
        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe(text);
        string.Concat(events.OfType<TextDeltaEvent>().Select(e => e.Delta)).ShouldBe(text);
        events.OfType<TextEndEvent>().ShouldHaveSingleItem();
        result.Usage.Input.ShouldBe(7);
        result.Usage.Output.ShouldBe(3);
    }

    [Fact]
    public async Task Stream_ToolUseWithMessageStop_PreservesShortProseAndToolCall()
    {
        var body = MessageStart() + TextStart() + TextDelta("PR") + BlockStop(0)
            + Frame("content_block_start", new
            {
                type = "content_block_start",
                index = 1,
                content_block = new { type = "tool_use", id = "toolu_terminal_4621", name = "read_file" }
            })
            + Frame("content_block_delta", new
            {
                type = "content_block_delta",
                index = 1,
                delta = new { type = "input_json_delta", partial_json = "{\"path\":\"Migration.md\"}" }
            })
            + BlockStop(1) + MessageDelta("tool_use") + MessageStop();

        var (events, result) = await RunProviderAsync(body);

        AssertCompleted(events, result, StopReason.ToolUse);
        result.Content.Count.ShouldBe(2);
        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("PR");
        var tool = result.Content.OfType<ToolCallContent>().ShouldHaveSingleItem();
        tool.Id.ShouldBe("toolu_terminal_4621");
        tool.Name.ShouldBe("read_file");
        tool.Arguments.ShouldContainKey("path");
        tool.Arguments["path"]?.ToString().ShouldBe("Migration.md");
        events.OfType<ToolCallStartEvent>().ShouldHaveSingleItem().ToolCallId.ShouldBe(tool.Id);
        events.OfType<ToolCallEndEvent>().ShouldHaveSingleItem().ToolCall.ShouldBe(tool);
    }

    [Fact]
    public async Task Stream_EofWithEarlierOpenAndLaterClosedText_PreservesWireOrderWithoutSyntheticEnd()
    {
        const string first = "first-open-block";
        const string second = "second-closed-block";
        var body = MessageStart() + TextStart() + TextDelta(first)
            + Frame("content_block_start", new
            {
                type = "content_block_start", index = 1, content_block = new { type = "text", text = "" }
            })
            + Frame("content_block_delta", new
            {
                type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = second }
            })
            + BlockStop(1);

        var (events, result) = await RunProviderAsync(body);

        events.OfType<DoneEvent>().ShouldBeEmpty();
        var error = events.OfType<ErrorEvent>().ShouldHaveSingleItem();
        events.Last().ShouldBeSameAs(error);
        error.Reason.ShouldBe(StopReason.Error);
        error.Error.ShouldBeSameAs(result);
        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldBe(IncompleteDiagnostic);
        result.ResponseId.ShouldBe("msg_terminal_4621");

        // Streaming order and persisted order must agree even when only the later block closed.
        events.OfType<TextStartEvent>().Select(e => e.ContentIndex).ShouldBe(new[] { 0, 1 });
        events.OfType<TextDeltaEvent>().Select(e => e.ContentIndex).ShouldBe(new[] { 0, 1 });
        events.OfType<TextDeltaEvent>().Select(e => e.Delta).ShouldBe(new[] { first, second });
        result.Content.Count.ShouldBe(2);
        result.Content.OfType<TextContent>().Select(c => c.Text).ShouldBe(new[] { first, second });
        string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text)).ShouldBe(first + second);

        // EOF preserves block zero's accumulated text but must not invent its content_block_stop.
        var closed = events.OfType<TextEndEvent>().ShouldHaveSingleItem();
        closed.ContentIndex.ShouldBe(1);
        closed.Content.ShouldBe(second);
        events.OfType<ThinkingEndEvent>().ShouldBeEmpty();
        events.OfType<ToolCallEndEvent>().ShouldBeEmpty();
    }

    private static void AssertCompleted(
        List<AssistantMessageEvent> events, AssistantMessage result, StopReason expectedReason)
    {
        events.OfType<ErrorEvent>().ShouldBeEmpty();
        var done = events.OfType<DoneEvent>().ShouldHaveSingleItem();
        events.Last().ShouldBeSameAs(done);
        done.Reason.ShouldBe(expectedReason);
        done.Message.ShouldBeSameAs(result);
        result.StopReason.ShouldBe(expectedReason);
        result.ErrorMessage.ShouldBeNull();
        result.ResponseId.ShouldBe("msg_terminal_4621");
    }

    private static string MessageStart() => Frame("message_start", new
    {
        type = "message_start",
        message = new { id = "msg_terminal_4621", usage = new { input_tokens = 7, output_tokens = 0 } }
    });

    private static string TextStart() => Frame("content_block_start", new
    {
        type = "content_block_start", index = 0, content_block = new { type = "text", text = "" }
    });

    private static string TextDelta(string text) => Frame("content_block_delta", new
    {
        type = "content_block_delta", index = 0, delta = new { type = "text_delta", text }
    });

    private static string BlockStop(int index) => Frame("content_block_stop", new
    {
        type = "content_block_stop", index
    });

    private static string MessageDelta(string stopReason) => Frame("message_delta", new
    {
        type = "message_delta", delta = new { stop_reason = stopReason }, usage = new { output_tokens = 3 }
    });

    private static string MessageStop() => Frame("message_stop", new { type = "message_stop" });

    private static string Frame(string eventType, object payload)
        => $"event: {eventType}\ndata: {JsonSerializer.Serialize(payload)}\n\n";

    private static async Task<(List<AssistantMessageEvent> Events, AssistantMessage Result)> RunProviderAsync(string body)
    {
        using var handler = new SseHandler(body);
        using var client = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new CopilotMessagesProvider(client);
        var model = new LlmModel("claude-terminal-test", "claude-terminal-test", CopilotMessagesProvider.ApiId,
            "github-copilot", "https://example.test", false, ["text"], new ModelCost(0, 0, 0, 0), 1000, 100);
        var context = new Context("system", [new UserMessage(new UserMessageContent("hello"), 1)], []);
        var stream = provider.Stream(model, context, new CopilotMessagesOptions
        {
            ApiKey = "synthetic-terminal-test-key", CancellationToken = timeout.Token
        });

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream.WithCancellation(timeout.Token))
            events.Add(evt);
        var result = await stream.GetResultAsync().WaitAsync(timeout.Token);

        // A request/setup error must not accidentally satisfy a missing-terminal assertion.
        handler.RequestCount.ShouldBe(1);
        events.OfType<StartEvent>().ShouldHaveSingleItem();
        return (events, result);
    }

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
