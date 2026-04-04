using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Streaming;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Streaming;

public class LlmStreamTests
{
    private static AssistantMessage MakeMessage(
        StopReason reason = StopReason.Stop,
        string? error = null,
        IReadOnlyList<ContentBlock>? content = null) => new(
        Content: content ?? [],
        Api: "test-api",
        Provider: "test",
        ModelId: "test-model",
        Usage: Usage.Empty(),
        StopReason: reason,
        ErrorMessage: error,
        ResponseId: null,
        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Fact]
    public async Task PushTextEvents_ConsumedViaAsyncEnumeration()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();

        stream.Push(new TextDeltaEvent(0, "hello", partial));
        stream.Push(new DoneEvent(StopReason.Stop, MakeMessage()));
        stream.End();

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<TextDeltaEvent>();
        events[1].Should().BeOfType<DoneEvent>();
    }

    [Fact]
    public async Task DoneEvent_TerminatesStream()
    {
        var stream = new LlmStream();
        var final = MakeMessage();

        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End();

        var count = 0;
        await foreach (var _ in stream)
            count++;

        count.Should().Be(1);
    }

    [Fact]
    public async Task ErrorEvent_TerminatesStream()
    {
        var stream = new LlmStream();
        var errorMsg = MakeMessage(StopReason.Error, "boom");

        stream.Push(new ErrorEvent(StopReason.Error, errorMsg));
        stream.End();

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        events.Should().ContainSingle();
        events[0].Should().BeOfType<ErrorEvent>();
    }

    [Fact]
    public async Task GetResultAsync_ReturnsFinalMessageOnDone()
    {
        var stream = new LlmStream();
        var final = MakeMessage();

        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End();

        var result = await stream.GetResultAsync();

        result.StopReason.Should().Be(StopReason.Stop);
        result.Api.Should().Be("test-api");
    }

    [Fact]
    public async Task GetResultAsync_ReturnsErrorMessageOnError()
    {
        var stream = new LlmStream();
        var errorMsg = MakeMessage(StopReason.Error, "failure");

        stream.Push(new ErrorEvent(StopReason.Error, errorMsg));
        stream.End();

        var result = await stream.GetResultAsync();

        result.StopReason.Should().Be(StopReason.Error);
        result.ErrorMessage.Should().Be("failure");
    }

    [Fact]
    public async Task MultipleEventsInSequence_AllConsumed()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();
        var final = MakeMessage(content: [new TextContent("done")]);

        stream.Push(new StartEvent(partial));
        stream.Push(new TextStartEvent(0, partial));
        stream.Push(new TextDeltaEvent(0, "hel", partial));
        stream.Push(new TextDeltaEvent(0, "lo", partial));
        stream.Push(new TextEndEvent(0, "hello", partial));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End();

        var types = new List<string>();
        await foreach (var evt in stream)
            types.Add(evt.Type);

        types.Should().Equal("start", "text_start", "text_delta", "text_delta", "text_end", "done");
    }

    [Fact]
    public async Task Stream_WithCancellationToken_Cancels()
    {
        var stream = new LlmStream();
        var cts = new CancellationTokenSource();
        var partial = MakeMessage();

        stream.Push(new TextDeltaEvent(0, "data", partial));

        cts.Cancel();

        var act = async () =>
        {
            await foreach (var _ in stream.WithCancellation(cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EmptyStream_JustDone()
    {
        var stream = new LlmStream();
        var final = MakeMessage();

        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End();

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<DoneEvent>();
    }

    [Fact]
    public async Task Stream_WithToolCallEvents_AllConsumed()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();
        var toolCall = new ToolCallContent("tc1", "read_file", new Dictionary<string, object?> { ["path"] = "/tmp" });
        var final = MakeMessage(content: [toolCall]);

        stream.Push(new ToolCallStartEvent(0, partial));
        stream.Push(new ToolCallDeltaEvent(0, "{\"path\":\"/tmp\"}", partial));
        stream.Push(new ToolCallEndEvent(0, toolCall, partial));
        stream.Push(new DoneEvent(StopReason.ToolUse, final));
        stream.End();

        var types = new List<string>();
        await foreach (var evt in stream)
            types.Add(evt.Type);

        types.Should().Equal("toolcall_start", "toolcall_delta", "toolcall_end", "done");
    }

    [Fact]
    public async Task Stream_WithThinkingEvents_AllConsumed()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();
        var final = MakeMessage(content: [new ThinkingContent("thought")]);

        stream.Push(new ThinkingStartEvent(0, partial));
        stream.Push(new ThinkingDeltaEvent(0, "thought", partial));
        stream.Push(new ThinkingEndEvent(0, "thought", partial));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End();

        var types = new List<string>();
        await foreach (var evt in stream)
            types.Add(evt.Type);

        types.Should().Equal("thinking_start", "thinking_delta", "thinking_end", "done");
    }
}
