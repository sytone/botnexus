using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests.Loop;

public class StreamAccumulatorTests
{
    [Fact]
    public async Task AccumulateAsync_StreamWithoutStartEvent_EmitsMessageStartThenMessageEnd()
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent("done")],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var eventTypes = new List<AgentEventType>();

        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);

        _ = await StreamAccumulator.AccumulateAsync(
            stream,
            evt =>
            {
                eventTypes.Add(evt.Type);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        eventTypes.Should().Equal(AgentEventType.MessageStart, AgentEventType.MessageEnd);
    }

    [Fact]
    public async Task AccumulateAsync_ErrorEventPreservesOriginalStopReason()
    {
        var stream = new LlmStream();
        var errorMessage = new AssistantMessage(
            Content: [new TextContent("aborted")],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.Aborted,
            ErrorMessage: "aborted",
            ResponseId: "resp_err",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new ErrorEvent(StopReason.Aborted, errorMessage));
        stream.End(errorMessage);

        var result = await StreamAccumulator.AccumulateAsync(stream, _ => Task.CompletedTask, CancellationToken.None);
        result.FinishReason.Should().Be(StopReason.Aborted);
    }

    [Fact]
    public async Task AccumulateAsync_UpdatesContextMessagesWithStreamingPartial()
    {
        var stream = new LlmStream();
        var start = CreateAssistantMessage("h");
        var partial = CreateAssistantMessage("hello");
        var final = CreateAssistantMessage("hello world");
        var contextMessages = new List<AgentMessage> { new BotNexus.Agent.Core.Types.UserMessage("prompt") };

        stream.Push(new StartEvent(start));
        stream.Push(new TextDeltaEvent(0, "ello", partial));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End(final);

        _ = await StreamAccumulator.AccumulateAsync(
            stream,
            _ => Task.CompletedTask,
            CancellationToken.None,
            contextMessages);

        contextMessages.Should().HaveCount(2);
        contextMessages[^1].Should().BeOfType<AssistantAgentMessage>()
            .Which.Content.Should().Be("hello world");
    }

    private static AssistantMessage CreateAssistantMessage(string content)
    {
        return new AssistantMessage(
            Content: [new TextContent(content)],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: "resp",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static AssistantMessage CreateErrorMessage(string error, StopReason reason = StopReason.Error)
    {
        return new AssistantMessage(
            Content: [],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: reason,
            ErrorMessage: error,
            ResponseId: "resp_err",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // --- Mid-stream error and edge case tests ---

    [Fact]
    public async Task AccumulateAsync_MidStreamError_ReplacesPartialWithFinalError()
    {
        var stream = new LlmStream();
        var partialMsg = CreateAssistantMessage("partial content");
        var errorMsg = CreateErrorMessage("connection lost");
        var contextMessages = new List<AgentMessage> { new BotNexus.Agent.Core.Types.UserMessage("prompt") };

        // Stream starts with content, then errors mid-stream
        stream.Push(new StartEvent(partialMsg));
        stream.Push(new TextDeltaEvent(0, "partial", partialMsg));
        stream.Push(new ErrorEvent(StopReason.Error, errorMsg));
        stream.End(errorMsg);

        var result = await StreamAccumulator.AccumulateAsync(
            stream,
            _ => Task.CompletedTask,
            CancellationToken.None,
            contextMessages);

        // Error should be the final state, not partial content
        result.FinishReason.Should().Be(StopReason.Error);
        result.ErrorMessage.Should().Be("connection lost");

        // Context should have exactly 2 messages (user + final assistant), not partial + final
        contextMessages.Should().HaveCount(2);
        var lastMessage = contextMessages[^1].Should().BeOfType<AssistantAgentMessage>().Subject;
        lastMessage.FinishReason.Should().Be(StopReason.Error);
    }

    [Fact]
    public async Task AccumulateAsync_EmptyStream_DoneOnly_ReturnsEmptyContent()
    {
        var stream = new LlmStream();
        var final = CreateAssistantMessage("");

        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End(final);

        var result = await StreamAccumulator.AccumulateAsync(stream, _ => Task.CompletedTask, CancellationToken.None);

        result.Content.Should().BeEmpty();
        result.FinishReason.Should().Be(StopReason.Stop);
    }

    [Fact]
    public async Task AccumulateAsync_MultipleTextDeltas_AccumulatesContent()
    {
        var stream = new LlmStream();
        var start = CreateAssistantMessage("");
        var final = CreateAssistantMessage("Hello World!");

        stream.Push(new StartEvent(start));
        stream.Push(new TextStartEvent(0, start));
        stream.Push(new TextDeltaEvent(0, "Hello", start));
        stream.Push(new TextDeltaEvent(0, " World", start));
        stream.Push(new TextDeltaEvent(0, "!", start));
        stream.Push(new TextEndEvent(0, "Hello World!", final));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End(final);

        var result = await StreamAccumulator.AccumulateAsync(stream, _ => Task.CompletedTask, CancellationToken.None);

        result.Content.Should().Be("Hello World!");
    }

    [Fact]
    public async Task AccumulateAsync_EmitsCorrectEventSequence_ForTextStream()
    {
        var stream = new LlmStream();
        var start = CreateAssistantMessage("Hello");
        var final = CreateAssistantMessage("Hello");
        var events = new List<AgentEventType>();

        stream.Push(new StartEvent(start));
        stream.Push(new TextDeltaEvent(0, "Hello", start));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End(final);

        await StreamAccumulator.AccumulateAsync(
            stream,
            evt => { events.Add(evt.Type); return Task.CompletedTask; },
            CancellationToken.None);

        events.Should().StartWith(AgentEventType.MessageStart);
        events.Should().Contain(AgentEventType.MessageUpdate);
        events.Should().EndWith(AgentEventType.MessageEnd);
    }

    [Fact]
    public async Task AccumulateAsync_ContextMessages_NotDuplicated_OnSuccessfulStream()
    {
        var stream = new LlmStream();
        var start = CreateAssistantMessage("reply");
        var final = CreateAssistantMessage("reply");
        var contextMessages = new List<AgentMessage>
        {
            new BotNexus.Agent.Core.Types.UserMessage("msg1"),
            new BotNexus.Agent.Core.Types.UserMessage("msg2")
        };

        stream.Push(new StartEvent(start));
        stream.Push(new TextDeltaEvent(0, "reply", start));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End(final);

        await StreamAccumulator.AccumulateAsync(
            stream,
            _ => Task.CompletedTask,
            CancellationToken.None,
            contextMessages);

        // Should add exactly 1 assistant message, not duplicate
        contextMessages.Should().HaveCount(3);
        contextMessages.OfType<AssistantAgentMessage>().Should().ContainSingle();
    }
}
