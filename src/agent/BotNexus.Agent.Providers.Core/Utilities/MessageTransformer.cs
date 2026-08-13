using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Utilities;

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
    /// - Drops orphaned tool results whose originating call is absent (#3014)
    /// - Skips errored/aborted assistant messages
    /// </summary>
    /// <param name="messages">Input messages to transform.</param>
    /// <param name="targetModel">Target model receiving the transformed messages.</param>
    /// <param name="normalizeToolCallId">
    /// Optional callback used to normalize tool-call IDs as:
    /// (callId, sourceModel, targetProviderId) => normalizedId.
    /// </param>
    public static List<Message> TransformMessages(
        IReadOnlyList<Message> messages,
        LlmModel targetModel,
        Func<string, LlmModel, string, string>? normalizeToolCallId = null)
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
        var seenToolCallIds = new HashSet<string>(StringComparer.Ordinal);
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

                    if (assistant.StopReason is StopReason.Error or StopReason.Aborted or StopReason.Refusal or StopReason.Sensitive)
                    {
                        continue;
                    }

                    pendingToolCalls = assistant.Content
                        .OfType<ToolCallContent>()
                        .ToList();

                    foreach (var call in pendingToolCalls)
                    {
                        seenToolCallIds.Add(BaseCallId(call.Id));
                    }

                    if (pendingToolCalls.Count > 0)
                    {
                        existingToolResultIds = new HashSet<string>(StringComparer.Ordinal);
                    }

                    result.Add(assistant);
                    break;

                case ToolResultMessage toolResult:
                    // ORPHAN-TOOL-RESULT-DROP-SITE (#3014). The single, shared place where a tool
                    // result whose originating call is absent from the retained transcript is
                    // dropped. Anthropic and the Copilot messages API reject such an orphan with a
                    // hard 400, and overflow compaction is exactly the moment one appears: the
                    // truncated tail can begin after the assistant turn that issued the call.
                    // Every provider converter routes through TransformMessages, so implementing
                    // the guard here - next to FlushOrphanedToolCalls, which owns the inverse
                    // calls-without-results direction - gives all four converters the same
                    // behaviour instead of one converter having it and three not.
                    if (!seenToolCallIds.Contains(BaseCallId(toolResult.ToolCallId)))
                    {
                        break;
                    }

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
        Func<string, LlmModel, string, string>? normalizeToolCallId,
        Dictionary<string, string> toolCallIdMap)
    {
        var isSameModel =
            string.Equals(assistant.Provider, targetModel.Provider, StringComparison.Ordinal) &&
            string.Equals(assistant.Api, targetModel.Api, StringComparison.Ordinal) &&
            string.Equals(assistant.ModelId, targetModel.Id, StringComparison.Ordinal);
        var sourceModel = targetModel with
        {
            Id = assistant.ModelId,
            Name = assistant.ModelId,
            Api = assistant.Api,
            Provider = assistant.Provider
        };

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
                        var normalizedId = normalizeToolCallId(toolCall.Id, sourceModel, targetModel.Provider);
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

    /// <summary>
    /// Returns the pairing key for a tool call id: the segment before the first <c>|</c>.
    /// Providers that carry a composite id (the Responses API packs <c>call_id|item_id</c>) must
    /// still pair against the base id, matching how the Responses converter derives the wire
    /// <c>call_id</c>. Ids without a pipe are their own base id.
    /// </summary>
    private static string BaseCallId(string id)
    {
        var pipe = id.IndexOf('|');
        return pipe < 0 ? id : id[..pipe];
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
