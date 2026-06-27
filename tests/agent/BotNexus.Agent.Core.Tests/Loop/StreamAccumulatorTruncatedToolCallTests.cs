using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Unit tests for #1666 at the accumulator boundary: when a streamed turn terminates with a
/// non-<see cref="StopReason.ToolUse"/> reason (truncation, content filter), the accumulated
/// final message must not carry tool-call content that a later code path could re-dispatch.
/// The visible text and the finish reason are retained -- only the tool-call content blocks
/// (both <see cref="AssistantAgentMessage.ToolCalls"/> and any tool-call block in
/// <see cref="AssistantAgentMessage.ContentBlocks"/>) are dropped.
/// </summary>
public class StreamAccumulatorTruncatedToolCallTests
{
    private static AssistantMessage BuildMessage(StopReason reason)
    {
        var content = new List<ContentBlock>
        {
            new TextContent("partial narration"),
            new ToolCallContent("call-1", "shell", new Dictionary<string, object?> { ["command"] = "gh issue cr" })
        };

        return new AssistantMessage(
            Content: content,
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: reason,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static LlmStream BuildStream(StopReason reason)
    {
        var message = BuildMessage(reason);
        var stream = new LlmStream();
        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, "partial narration", message));
        stream.Push(new TextEndEvent(0, "partial narration", message));
        stream.Push(new ToolCallStartEvent(1, message));
        stream.Push(new ToolCallDeltaEvent(1, "{\"command\":\"gh issue cr", message));
        stream.Push(new ToolCallEndEvent(1, (ToolCallContent)message.Content[1], message));
        stream.Push(new DoneEvent(reason, message));
        stream.End(message);
        return stream;
    }

    /// <summary>
    /// A truncated (<see cref="StopReason.Length"/>) terminal that surfaced a tool call must
    /// have the tool call stripped from the accumulated message while text and finish reason
    /// are preserved.
    /// </summary>
    [Fact]
    public async Task AccumulateAsync_LengthTerminalWithToolCall_StripsToolCallButKeepsTextAndReason()
    {
        var contextMessages = new List<AgentMessage> { new BotNexus.Agent.Core.Types.UserMessage("prompt") };

        var result = await StreamAccumulator.AccumulateAsync(
            BuildStream(StopReason.Length),
            _ => Task.CompletedTask,
            CancellationToken.None,
            contextMessages);

        result.FinishReason.ShouldBe(StopReason.Length);
        result.Content.ShouldContain("partial narration");
        result.ToolCalls.ShouldBeNull();
        (result.ContentBlocks ?? []).OfType<ToolCallContent>().ShouldBeEmpty();
        (result.ContentBlocks ?? []).OfType<TextContent>().ShouldNotBeEmpty();

        // The message recorded into the timeline is the stripped final, not the raw partial.
        var recorded = contextMessages[^1].ShouldBeOfType<AssistantAgentMessage>();
        recorded.ToolCalls.ShouldBeNull();
        (recorded.ContentBlocks ?? []).OfType<ToolCallContent>().ShouldBeEmpty();
    }

    /// <summary>
    /// A content-filter terminal (<see cref="StopReason.Sensitive"/>) is treated the same way
    /// -- any surfaced tool call is non-executable and stripped.
    /// </summary>
    [Fact]
    public async Task AccumulateAsync_SensitiveTerminalWithToolCall_StripsToolCall()
    {
        var result = await StreamAccumulator.AccumulateAsync(
            BuildStream(StopReason.Sensitive),
            _ => Task.CompletedTask,
            CancellationToken.None);

        result.FinishReason.ShouldBe(StopReason.Sensitive);
        result.ToolCalls.ShouldBeNull();
        (result.ContentBlocks ?? []).OfType<ToolCallContent>().ShouldBeEmpty();
    }

    /// <summary>
    /// A legitimate <see cref="StopReason.ToolUse"/> terminal keeps its tool call intact --
    /// the strip must not regress real tool turns.
    /// </summary>
    [Fact]
    public async Task AccumulateAsync_ToolUseTerminal_RetainsToolCall()
    {
        var result = await StreamAccumulator.AccumulateAsync(
            BuildStream(StopReason.ToolUse),
            _ => Task.CompletedTask,
            CancellationToken.None);

        result.FinishReason.ShouldBe(StopReason.ToolUse);
        result.ToolCalls.ShouldNotBeNull();
        result.ToolCalls!.ShouldHaveSingleItem();
        (result.ContentBlocks ?? []).OfType<ToolCallContent>().ShouldHaveSingleItem();
    }
}
