using BotNexus.Providers.Core.Models;

namespace BotNexus.Providers.Core.Utilities;

/// <summary>
/// Cross-provider message transformation.
/// Port of pi-mono's providers/transform-messages.ts.
/// </summary>
public static class MessageTransformer
{
    /// <summary>
    /// Transform messages for cross-provider compatibility.
    /// - Converts thinking blocks to text when switching providers
    /// - Normalizes tool call IDs
    /// - Inserts synthetic tool results for orphaned tool calls
    /// - Skips errored/aborted assistant messages
    /// </summary>
    public static List<Message> TransformMessages(
        IReadOnlyList<Message> messages,
        LlmModel targetModel,
        Func<string, string>? normalizeToolCallId = null)
    {
        var transformed = new List<Message>(messages.Count);
        var toolCallIdMap = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage:
                    transformed.Add(message);
                    break;

                case AssistantMessage assistant:
                    transformed.Add(TransformAssistantMessage(assistant, targetModel, normalizeToolCallId, toolCallIdMap));
                    break;

                case ToolResultMessage toolResult:
                    if (toolCallIdMap.TryGetValue(toolResult.ToolCallId, out var normalizedId) &&
                        !string.Equals(normalizedId, toolResult.ToolCallId, StringComparison.Ordinal))
                    {
                        transformed.Add(toolResult with { ToolCallId = normalizedId });
                    }
                    else
                    {
                        transformed.Add(toolResult);
                    }
                    break;

                default:
                    transformed.Add(message);
                    break;
            }
        }

        var result = new List<Message>(transformed.Count);
        var pendingToolCalls = new List<ToolCallContent>();
        var existingToolResultIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in transformed)
        {
            switch (message)
            {
                case AssistantMessage assistant:
                    if (pendingToolCalls.Count > 0)
                    {
                        FlushOrphanedToolCalls(result, pendingToolCalls, existingToolResultIds);
                    }

                    if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
                    {
                        continue;
                    }

                    pendingToolCalls = assistant.Content
                        .OfType<ToolCallContent>()
                        .ToList();
                    if (pendingToolCalls.Count > 0)
                    {
                        existingToolResultIds = new HashSet<string>(StringComparer.Ordinal);
                    }

                    result.Add(assistant);
                    break;

                case ToolResultMessage toolResult:
                    existingToolResultIds.Add(toolResult.ToolCallId);
                    result.Add(toolResult);
                    break;

                case UserMessage:
                    if (pendingToolCalls.Count > 0)
                    {
                        FlushOrphanedToolCalls(result, pendingToolCalls, existingToolResultIds);
                    }

                    result.Add(message);
                    break;

                default:
                    result.Add(message);
                    break;
            }
        }

        return result;
    }

    private static AssistantMessage TransformAssistantMessage(
        AssistantMessage assistant,
        LlmModel targetModel,
        Func<string, string>? normalizeToolCallId,
        Dictionary<string, string> toolCallIdMap)
    {
        var isSameModel =
            string.Equals(assistant.Provider, targetModel.Provider, StringComparison.Ordinal) &&
            string.Equals(assistant.Api, targetModel.Api, StringComparison.Ordinal) &&
            string.Equals(assistant.ModelId, targetModel.Id, StringComparison.Ordinal);

        var transformedContent = new List<ContentBlock>(assistant.Content.Count);

        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case ThinkingContent thinking:
                    if (thinking.Redacted is true)
                    {
                        if (isSameModel)
                        {
                            transformedContent.Add(thinking);
                        }

                        break;
                    }

                    if (isSameModel && !string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                    {
                        transformedContent.Add(thinking);
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(thinking.Thinking))
                    {
                        break;
                    }

                    if (isSameModel)
                    {
                        transformedContent.Add(thinking);
                        break;
                    }

                    transformedContent.Add(new TextContent(thinking.Thinking));
                    break;

                case TextContent text:
                    transformedContent.Add(isSameModel ? text : new TextContent(text.Text));
                    break;

                case ToolCallContent toolCall:
                    var transformedToolCall = toolCall;

                    if (!isSameModel && !string.IsNullOrWhiteSpace(transformedToolCall.ThoughtSignature))
                    {
                        transformedToolCall = transformedToolCall with { ThoughtSignature = null };
                    }

                    if (!isSameModel && normalizeToolCallId is not null)
                    {
                        var normalizedId = normalizeToolCallId(toolCall.Id);
                        if (!string.Equals(normalizedId, toolCall.Id, StringComparison.Ordinal))
                        {
                            toolCallIdMap[toolCall.Id] = normalizedId;
                            transformedToolCall = transformedToolCall with { Id = normalizedId };
                        }
                    }

                    transformedContent.Add(transformedToolCall);
                    break;

                default:
                    transformedContent.Add(block);
                    break;
            }
        }

        return assistant with { Content = transformedContent };
    }

    private static void FlushOrphanedToolCalls(
        List<Message> result,
        List<ToolCallContent> pendingToolCalls,
        HashSet<string> existingToolResultIds)
    {
        if (pendingToolCalls.Count == 0)
            return;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var toolCall in pendingToolCalls)
        {
            if (existingToolResultIds.Contains(toolCall.Id))
            {
                continue;
            }

            result.Add(new ToolResultMessage(
                ToolCallId: toolCall.Id,
                ToolName: toolCall.Name,
                Content: [new TextContent("No result provided")],
                IsError: true,
                Timestamp: timestamp));
        }

        pendingToolCalls.Clear();
        existingToolResultIds.Clear();
    }
}
