using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Anthropic;

/// <summary>
/// Parses Anthropic SSE streams into content blocks and streaming events.
/// </summary>
internal static class AnthropicStreamParser
{
    internal static async Task<(Usage Usage, string? ResponseId, StopReason StopReason)> ProcessStreamAsync(
        StreamReader reader,
        LlmModel model,
        LlmStream stream,
        List<ContentBlock> contentBlocks,
        Usage initialUsage,
        IReadOnlyList<Tool>? tools,
        bool isOAuthToken,
        Func<LlmModel, List<ContentBlock>, Usage, StopReason, string?, string?, AssistantMessage> buildMessage,
        Func<string?, StopReason> mapStopReason,
        CancellationToken ct)
    {
        var usage = initialUsage;
        var blockTypes = new Dictionary<int, string>();
        var textAccumulators = new Dictionary<int, StringBuilder>();
        var signatureAccumulators = new Dictionary<int, StringBuilder>();
        var toolCallIds = new Dictionary<int, string>();
        var toolCallNames = new Dictionary<int, string>();
        string? responseId = null;
        var stopReason = StopReason.Stop;
        string? currentEvent = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line[6..].TrimStart();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].TrimStart();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }

            using (doc)
            {
                ProcessSseEvent(
                    currentEvent,
                    doc.RootElement,
                    model,
                    stream,
                    contentBlocks,
                    blockTypes,
                    textAccumulators,
                    signatureAccumulators,
                    toolCallIds,
                    toolCallNames,
                    usage,
                    tools,
                    isOAuthToken,
                    buildMessage,
                    mapStopReason,
                    ref responseId,
                    ref stopReason,
                    out usage);
            }

            currentEvent = null;
        }

        return (usage, responseId, stopReason);
    }

    private static void ProcessSseEvent(
        string? eventType,
        JsonElement data,
        LlmModel model,
        LlmStream stream,
        List<ContentBlock> contentBlocks,
        Dictionary<int, string> blockTypes,
        Dictionary<int, StringBuilder> textAccumulators,
        Dictionary<int, StringBuilder> signatureAccumulators,
        Dictionary<int, string> toolCallIds,
        Dictionary<int, string> toolCallNames,
        Usage usage,
        IReadOnlyList<Tool>? tools,
        bool isOAuthToken,
        Func<LlmModel, List<ContentBlock>, Usage, StopReason, string?, string?, AssistantMessage> buildMessage,
        Func<string?, StopReason> mapStopReason,
        ref string? responseId,
        ref StopReason stopReason,
        out Usage updatedUsage)
    {
        updatedUsage = usage;
        var type = data.TryGetProperty("type", out var typeProp)
            ? typeProp.GetString()
            : eventType;

        switch (type)
        {
            case "message_start":
                if (data.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("id", out var id))
                        responseId = id.GetString();
                    if (msg.TryGetProperty("usage", out var msgUsage))
                        updatedUsage = UpdateUsage(updatedUsage, msgUsage);
                }
                stream.Push(new StartEvent(
                    buildMessage(model, contentBlocks, updatedUsage, StopReason.Stop, null, responseId)));
                break;

            case "content_block_start":
                HandleContentBlockStart(data, model, stream, contentBlocks, blockTypes,
                    textAccumulators, signatureAccumulators, toolCallIds, toolCallNames, usage, responseId,
                    tools, isOAuthToken, buildMessage);
                break;

            case "content_block_delta":
                HandleContentBlockDelta(data, model, stream, contentBlocks, blockTypes,
                    textAccumulators, signatureAccumulators, usage, responseId, buildMessage);
                break;

            case "content_block_stop":
                HandleContentBlockStop(data, model, stream, contentBlocks, blockTypes,
                    textAccumulators, signatureAccumulators, toolCallIds, toolCallNames, usage, responseId, buildMessage);
                break;

            case "message_delta":
                if (data.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("stop_reason", out var sr))
                {
                    stopReason = mapStopReason(sr.GetString());
                }
                if (data.TryGetProperty("usage", out var deltaUsage))
                    updatedUsage = UpdateUsage(updatedUsage, deltaUsage);
                break;

            case "message_stop":
                break;

            case "error":
                var errorMsg = data.TryGetProperty("error", out var err)
                    ? err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error"
                    : "Unknown error";
                throw new InvalidOperationException($"Anthropic streaming error: {errorMsg}");
        }
    }

    private static void HandleContentBlockStart(
        JsonElement data,
        LlmModel model,
        LlmStream stream,
        List<ContentBlock> contentBlocks,
        Dictionary<int, string> blockTypes,
        Dictionary<int, StringBuilder> textAccumulators,
        Dictionary<int, StringBuilder> signatureAccumulators,
        Dictionary<int, string> toolCallIds,
        Dictionary<int, string> toolCallNames,
        Usage usage,
        string? responseId,
        IReadOnlyList<Tool>? tools,
        bool isOAuthToken,
        Func<LlmModel, List<ContentBlock>, Usage, StopReason, string?, string?, AssistantMessage> buildMessage)
    {
        var index = data.GetProperty("index").GetInt32();
        if (!data.TryGetProperty("content_block", out var block)) return;

        var blockType = block.GetProperty("type").GetString() ?? "text";
        blockTypes[index] = blockType;
        textAccumulators[index] = new StringBuilder();
        signatureAccumulators[index] = new StringBuilder();

        var partial = buildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);

        switch (blockType)
        {
            case "text":
                stream.Push(new TextStartEvent(index, partial));
                break;
            case "thinking":
                stream.Push(new ThinkingStartEvent(index, partial));
                break;
            case "redacted_thinking":
                if (block.TryGetProperty("data", out var redactedData))
                    signatureAccumulators[index].Append(redactedData.GetString());
                textAccumulators[index].Append("[Reasoning redacted]");
                stream.Push(new ThinkingStartEvent(index, partial));
                break;
            case "tool_use":
                if (block.TryGetProperty("id", out var tcId))
                    toolCallIds[index] = tcId.GetString() ?? "";
                if (block.TryGetProperty("name", out var tcName))
                    toolCallNames[index] = isOAuthToken
                        ? AnthropicMessageConverter.FromClaudeCodeName(tcName.GetString() ?? "", tools)
                        : tcName.GetString() ?? "";
                stream.Push(new ToolCallStartEvent(index, partial));
                break;
        }
    }

    private static void HandleContentBlockDelta(
        JsonElement data,
        LlmModel model,
        LlmStream stream,
        List<ContentBlock> contentBlocks,
        Dictionary<int, string> blockTypes,
        Dictionary<int, StringBuilder> textAccumulators,
        Dictionary<int, StringBuilder> signatureAccumulators,
        Usage usage,
        string? responseId,
        Func<LlmModel, List<ContentBlock>, Usage, StopReason, string?, string?, AssistantMessage> buildMessage)
    {
        var index = data.GetProperty("index").GetInt32();
        if (!data.TryGetProperty("delta", out var delta)) return;

        var deltaType = delta.GetProperty("type").GetString();
        var partial = buildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);

        switch (deltaType)
        {
            case "text_delta":
                var text = delta.GetProperty("text").GetString() ?? "";
                textAccumulators[index].Append(text);
                stream.Push(new TextDeltaEvent(index, text, partial));
                break;
            case "thinking_delta":
                var thinking = delta.GetProperty("thinking").GetString() ?? "";
                textAccumulators[index].Append(thinking);
                stream.Push(new ThinkingDeltaEvent(index, thinking, partial));
                break;
            case "input_json_delta":
                var jsonFrag = delta.GetProperty("partial_json").GetString() ?? "";
                textAccumulators[index].Append(jsonFrag);
                stream.Push(new ToolCallDeltaEvent(index, jsonFrag, partial));
                break;
            case "signature_delta":
                var sig = delta.GetProperty("signature").GetString() ?? "";
                signatureAccumulators[index].Append(sig);
                break;
        }
    }

    private static void HandleContentBlockStop(
        JsonElement data,
        LlmModel model,
        LlmStream stream,
        List<ContentBlock> contentBlocks,
        Dictionary<int, string> blockTypes,
        Dictionary<int, StringBuilder> textAccumulators,
        Dictionary<int, StringBuilder> signatureAccumulators,
        Dictionary<int, string> toolCallIds,
        Dictionary<int, string> toolCallNames,
        Usage usage,
        string? responseId,
        Func<LlmModel, List<ContentBlock>, Usage, StopReason, string?, string?, AssistantMessage> buildMessage)
    {
        var index = data.GetProperty("index").GetInt32();
        if (!blockTypes.TryGetValue(index, out var blockType)) return;

        var accumulated = textAccumulators.GetValueOrDefault(index)?.ToString() ?? "";
        var signature = signatureAccumulators.GetValueOrDefault(index)?.ToString();
        if (string.IsNullOrEmpty(signature)) signature = null;

        switch (blockType)
        {
            case "text":
                contentBlocks.Add(new TextContent(accumulated, signature));
                var textPartial = buildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new TextEndEvent(index, accumulated, textPartial));
                break;

            case "thinking":
                contentBlocks.Add(new ThinkingContent(accumulated, signature));
                var thinkPartial = buildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new ThinkingEndEvent(index, accumulated, thinkPartial));
                break;

            case "redacted_thinking":
                contentBlocks.Add(new ThinkingContent(accumulated, signature, Redacted: true));
                var redactPartial = buildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new ThinkingEndEvent(index, accumulated, redactPartial));
                break;

            case "tool_use":
                var toolId = toolCallIds.GetValueOrDefault(index, "");
                var toolName = toolCallNames.GetValueOrDefault(index, "");
                var args = StreamingJsonParser.Parse(accumulated);
                var toolCall = new ToolCallContent(toolId, toolName, args, signature);
                contentBlocks.Add(toolCall);
                var toolPartial = buildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new ToolCallEndEvent(index, toolCall, toolPartial));
                break;
        }
    }

    private static Usage UpdateUsage(Usage usage, JsonElement usageElement)
    {
        var updated = usage;
        if (usageElement.TryGetProperty("input_tokens", out var it))
            updated = updated with { Input = it.GetInt32() };
        if (usageElement.TryGetProperty("output_tokens", out var ot))
            updated = updated with { Output = ot.GetInt32() };
        if (usageElement.TryGetProperty("cache_read_input_tokens", out var cr))
            updated = updated with { CacheRead = cr.GetInt32() };
        if (usageElement.TryGetProperty("cache_creation_input_tokens", out var cw))
            updated = updated with { CacheWrite = cw.GetInt32() };
        return updated;
    }
}
