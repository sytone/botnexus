using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Streaming;

namespace BotNexus.AgentCore.Loop;

internal static class StreamAccumulator
{
    public static async Task<AssistantAgentMessage> AccumulateAsync(
        LlmStream stream,
        Func<AgentEvent, Task> emit,
        CancellationToken cancellationToken)
    {
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
                    break;

                case TextDeltaEvent textDelta:
                    current = MessageConverter.ToAgentMessage(textDelta.Partial);
                    await emit(new MessageUpdateEvent(
                        Message: current,
                        ContentDelta: textDelta.Delta,
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
                    await emit(new MessageEndEvent(final, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;

                case ErrorEvent error:
                    final = MessageConverter.ToAgentMessage(error.Error) with { FinishReason = StopReason.Error };
                    await emit(new MessageEndEvent(final, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                    break;
            }
        }

        if (final is not null)
        {
            return final;
        }

        var result = await stream.GetResultAsync().ConfigureAwait(false);
        return MessageConverter.ToAgentMessage(result);
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
