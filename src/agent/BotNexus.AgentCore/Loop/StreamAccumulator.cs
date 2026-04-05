using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Streaming;

namespace BotNexus.AgentCore.Loop;

/// <summary>
/// Accumulates streaming LLM events into a final AssistantAgentMessage.
/// </summary>
/// <remarks>
/// Handles StartEvent, text/thinking/tool-call delta lifecycle events, DoneEvent, and ErrorEvent.
/// Emits corresponding AgentEvent for each provider event.
/// Maintains tool call state to correlate deltas with tool IDs/names.
/// </remarks>
internal static class StreamAccumulator
{
    /// <summary>
    /// Consume a provider LLM stream and emit agent events.
    /// </summary>
    /// <param name="stream">The provider event stream.</param>
    /// <param name="emit">The event emission callback.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The final accumulated assistant message.</returns>
    /// <remarks>
    /// Emits MessageStartEvent → MessageUpdateEvent (0+) → MessageEndEvent.
    /// Returns the completed message from DoneEvent or ErrorEvent.
    /// </remarks>
    public static async Task<AssistantAgentMessage> AccumulateAsync(
        LlmStream stream,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var _startEmitted = false;
        AssistantAgentMessage? current = null;
        AssistantAgentMessage? final = null;
        var toolCallState = new Dictionary<int, (string? Id, string? Name)>();

        await foreach (var evt in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (evt)
            {
                case StartEvent start:
                    current = MessageConverter.ToAgentMessage(start.Partial);
                    await emit(new MessageStartEvent(current, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    _startEmitted = true;
                    break;

                case TextStartEvent textStart:
                    current = MessageConverter.ToAgentMessage(textStart.Partial);
                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: null,
                        IsThinking: false,
                        ToolCallId: null,
                        ToolName: null,
                        ArgumentsDelta: null,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case TextDeltaEvent textDelta:
                    current = MessageConverter.ToAgentMessage(textDelta.Partial);
                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: textDelta.Delta,
                        IsThinking: false,
                        ToolCallId: null,
                        ToolName: null,
                        ArgumentsDelta: null,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case TextEndEvent textEnd:
                    current = MessageConverter.ToAgentMessage(textEnd.Partial);
                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: null,
                        IsThinking: false,
                        ToolCallId: null,
                        ToolName: null,
                        ArgumentsDelta: null,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case ThinkingStartEvent thinkingStart:
                    current = MessageConverter.ToAgentMessage(thinkingStart.Partial);
                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: null,
                        IsThinking: true,
                        ToolCallId: null,
                        ToolName: null,
                        ArgumentsDelta: null,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case ThinkingDeltaEvent thinkingDelta:
                    current = MessageConverter.ToAgentMessage(thinkingDelta.Partial);
                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: thinkingDelta.Delta,
                        IsThinking: true,
                        ToolCallId: null,
                        ToolName: null,
                        ArgumentsDelta: null,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case ThinkingEndEvent thinkingEnd:
                    current = MessageConverter.ToAgentMessage(thinkingEnd.Partial);
                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: null,
                        IsThinking: true,
                        ToolCallId: null,
                        ToolName: null,
                        ArgumentsDelta: null,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case ToolCallStartEvent toolStart:
                    current = MessageConverter.ToAgentMessage(toolStart.Partial);
                    toolCallState[toolStart.ContentIndex] = ResolveToolCallIdentity(current, toolStart.ContentIndex);
                    var startedTool = toolCallState[toolStart.ContentIndex];

                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: null,
                        IsThinking: false,
                        ToolCallId: startedTool.Id,
                        ToolName: startedTool.Name,
                        ArgumentsDelta: null,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case ToolCallDeltaEvent toolDelta:
                    current = MessageConverter.ToAgentMessage(toolDelta.Partial);
                    if (!toolCallState.TryGetValue(toolDelta.ContentIndex, out var deltaTool))
                    {
                        deltaTool = ResolveToolCallIdentity(current, toolDelta.ContentIndex);
                        toolCallState[toolDelta.ContentIndex] = deltaTool;
                    }

                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: null,
                        IsThinking: false,
                        ToolCallId: deltaTool.Id,
                        ToolName: deltaTool.Name,
                        ArgumentsDelta: toolDelta.Delta,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case ToolCallEndEvent toolEnd:
                    current = MessageConverter.ToAgentMessage(toolEnd.Partial);
                    toolCallState[toolEnd.ContentIndex] = (toolEnd.ToolCall.Id, toolEnd.ToolCall.Name);

                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: null,
                        IsThinking: false,
                        ToolCallId: toolEnd.ToolCall.Id,
                        ToolName: toolEnd.ToolCall.Name,
                        ArgumentsDelta: null,
                        FinishReason: null,
                        InputTokens: current.Usage?.InputTokens,
                        OutputTokens: current.Usage?.OutputTokens,
                        Timestamp: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case DoneEvent done:
                    final = MessageConverter.ToAgentMessage(done.Message);
                    if (!_startEmitted)
                    {
                        await emit(new MessageStartEvent(final, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                        _startEmitted = true;
                    }

                    await emit(new MessageEndEvent(final, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case ErrorEvent error:
                    final = MessageConverter.ToAgentMessage(error.Error) with { FinishReason = StopReason.Error };
                    if (!_startEmitted)
                    {
                        await emit(new MessageStartEvent(final, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                        _startEmitted = true;
                    }

                    await emit(new MessageEndEvent(final, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;
            }
        }

        if (final is not null)
        {
            return final;
        }

        var result = await stream.GetResultAsync().ConfigureAwait(false);
        final = MessageConverter.ToAgentMessage(result);

        if (!_startEmitted)
        {
            await emit(new MessageStartEvent(final, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }

        await emit(new MessageEndEvent(final, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        return final;
    }

    private static (string? Id, string? Name) ResolveToolCallIdentity(
        AssistantAgentMessage message,
        int contentIndex)
    {
        if (message.ToolCalls is null || message.ToolCalls.Count == 0)
        {
            return (null, null);
        }

        if (contentIndex >= 0 && contentIndex < message.ToolCalls.Count)
        {
            var directMatch = message.ToolCalls[contentIndex];
            return (directMatch.Id, directMatch.Name);
        }

        var latest = message.ToolCalls[^1];
        return (latest.Id, latest.Name);
    }
}
