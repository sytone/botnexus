using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Copilot.Responses;

/// <summary>
/// Parses a GitHub Copilot Responses API SSE stream into content blocks and streaming events.
/// Extracted verbatim from <c>CopilotResponsesProvider.ParseSseStream</c> (#1404 step 2/6 of #1377)
/// so the single most behaviorally-sensitive block lives in one focused, testable seam. This is a
/// pure move: the parsing logic and its exclusive helpers are unchanged (including the Copilot usage
/// telemetry hook and the Copilot service-tier resolution); the provider supplies its <c>Api</c>
/// string, logger, and error-emit callback as parameters. The shared <see cref="SseEvent"/>,
/// <see cref="ToolState"/>, and pure helpers (<c>ParseUsage</c>/<c>MapStopReason</c>/<c>ComposeToolCallId</c>)
/// now live in <c>BotNexus.Agent.Providers.Core.Streaming</c> (step 5/6 of #1377), shared with the OpenAI
/// Responses parser. <c>CopilotResponsesProviderParityTests</c> guards behavior preservation.
/// </summary>
internal static class CopilotResponsesStreamParser
{
    internal static async Task ParseAsync(
        LlmStream stream,
        StreamReader reader,
        LlmModel model,
        StreamOptions? options,
        string api,
        ILogger logger,
        Action<LlmStream, LlmModel, string, IReadOnlyList<ContentBlock>?> emitError,
        CancellationToken ct)
    {
        var contentBlocks = new List<ContentBlock>();
        var usage = Usage.Empty();
        string? responseId = null;
        var started = false;
        var stopReason = StopReason.Stop;
        var sawRefusal = false;

        var textStates = new Dictionary<string, (int ContentIndex, StringBuilder Text)>(StringComparer.Ordinal);
        var thinkingStates = new Dictionary<string, (int ContentIndex, StringBuilder Text)>(StringComparer.Ordinal);
        var toolStates = new Dictionary<string, ToolState>(StringComparer.Ordinal);

        AssistantMessage BuildPartial() => new(
            Content: contentBlocks.ToList(),
            Api: api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: usage,
            StopReason: stopReason,
            ErrorMessage: null,
            ResponseId: responseId,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        void EnsureStart()
        {
            if (started) return;
            stream.Push(new StartEvent(BuildPartial()));
            started = true;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var evt = await ReadSseEventAsync(reader, ct);
            if (evt is null) break;

            if (string.Equals(evt.Event, "error", StringComparison.Ordinal))
            {
                emitError(stream, model, evt.Data, null);
                return;
            }

            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(evt.Data);
            }
            catch (JsonException)
            {
                logger.LogDebug("Skipping malformed responses SSE event {Event}", evt.Event);
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                Telemetry.CopilotUsageActivity.TryParseAndEmit(root, Activity.Current);

                if (evt.Event is "response.created")
                {
                    if (root.TryGetProperty("response", out var responseEl))
                        responseId = GetString(responseEl, "id") ?? responseId;
                    continue;
                }

                if (evt.Event is "response.output_item.added")
                {
                    EnsureStart();
                    if (!root.TryGetProperty("item", out var item)) continue;
                    var itemType = GetString(item, "type");
                    var itemId = GetString(item, "id");

                    switch (itemType)
                    {
                        case "reasoning":
                        {
                            var index = contentBlocks.Count;
                            contentBlocks.Add(new ThinkingContent(""));
                            if (!string.IsNullOrWhiteSpace(itemId))
                                thinkingStates[itemId] = (index, new StringBuilder());
                            stream.Push(new ThinkingStartEvent(index, BuildPartial()));
                            break;
                        }
                        case "message":
                        {
                            var index = contentBlocks.Count;
                            contentBlocks.Add(new TextContent(""));
                            if (!string.IsNullOrWhiteSpace(itemId))
                                textStates[itemId] = (index, new StringBuilder());
                            stream.Push(new TextStartEvent(index, BuildPartial()));
                            break;
                        }
                        case "function_call":
                        {
                            var callId = GetString(item, "call_id") ?? "";
                            var name = GetString(item, "name") ?? "";
                            var arguments = GetString(item, "arguments") ?? "";
                            var index = contentBlocks.Count;
                            var parsed = StreamingJsonParser.Parse(arguments);
                            contentBlocks.Add(new ToolCallContent(ResponsesStreamHelpers.ComposeToolCallId(callId, itemId), name, parsed));
                            stream.Push(new ToolCallStartEvent(index, BuildPartial()));

                            var state = new ToolState(callId, itemId, name, index);
                            state.Arguments.Append(arguments);
                            toolStates[callId] = state;

                            if (arguments.Length > 0)
                                stream.Push(new ToolCallDeltaEvent(index, arguments, BuildPartial()));
                            break;
                        }
                    }

                    continue;
                }

                if (evt.Event is "response.reasoning_summary_text.delta")
                {
                    EnsureStart();
                    var itemId = GetString(root, "item_id");
                    if (itemId is null || !thinkingStates.TryGetValue(itemId, out var state)) continue;
                    var delta = GetString(root, "delta") ?? "";
                    if (delta.Length == 0) continue;
                    state.Text.Append(delta);
                    contentBlocks[state.ContentIndex] = new ThinkingContent(state.Text.ToString());
                    stream.Push(new ThinkingDeltaEvent(state.ContentIndex, delta, BuildPartial()));
                    thinkingStates[itemId] = state;
                    continue;
                }

                if (evt.Event is "response.reasoning_summary_part.done")
                {
                    var itemId = GetString(root, "item_id");
                    if (itemId is null || !thinkingStates.TryGetValue(itemId, out var state)) continue;
                    state.Text.Append("\n\n");
                    contentBlocks[state.ContentIndex] = new ThinkingContent(state.Text.ToString());
                    stream.Push(new ThinkingDeltaEvent(state.ContentIndex, "\n\n", BuildPartial()));
                    thinkingStates[itemId] = state;
                    continue;
                }

                if (evt.Event is "response.output_text.delta" or "response.refusal.delta")
                {
                    EnsureStart();
                    if (evt.Event is "response.refusal.delta")
                    {
                        sawRefusal = true;
                        stopReason = StopReason.Refusal;
                    }
                    var itemId = GetString(root, "item_id");
                    var delta = GetString(root, "delta") ?? "";
                    if (delta.Length == 0) continue;

                    if (itemId is null || !textStates.TryGetValue(itemId, out var state))
                    {
                        var index = contentBlocks.Count;
                        contentBlocks.Add(new TextContent(""));
                        state = (index, new StringBuilder());
                        textStates[itemId ?? Guid.NewGuid().ToString("N")] = state;
                        stream.Push(new TextStartEvent(index, BuildPartial()));
                    }

                    state.Text.Append(delta);
                    contentBlocks[state.ContentIndex] = new TextContent(state.Text.ToString());
                    stream.Push(new TextDeltaEvent(state.ContentIndex, delta, BuildPartial()));
                    if (itemId is not null)
                        textStates[itemId] = state;
                    continue;
                }

                if (evt.Event is "response.function_call_arguments.delta")
                {
                    EnsureStart();
                    var callId = GetString(root, "call_id");
                    var delta = GetString(root, "delta") ?? "";
                    if (callId is null || delta.Length == 0 || !toolStates.TryGetValue(callId, out var state)) continue;

                    state.Arguments.Append(delta);
                    contentBlocks[state.ContentIndex] = new ToolCallContent(
                        ResponsesStreamHelpers.ComposeToolCallId(state.CallId, state.ItemId),
                        state.Name,
                        StreamingJsonParser.Parse(state.Arguments.ToString()));
                    stream.Push(new ToolCallDeltaEvent(state.ContentIndex, delta, BuildPartial()));
                    continue;
                }

                if (evt.Event is "response.function_call_arguments.done")
                {
                    var callId = GetString(root, "call_id");
                    var finalArgs = GetString(root, "arguments") ?? "";
                    if (callId is null || !toolStates.TryGetValue(callId, out var state)) continue;
                    var before = state.Arguments.ToString();
                    state.Arguments.Clear();
                    state.Arguments.Append(finalArgs);
                    contentBlocks[state.ContentIndex] = new ToolCallContent(
                        ResponsesStreamHelpers.ComposeToolCallId(state.CallId, state.ItemId),
                        state.Name,
                        StreamingJsonParser.Parse(finalArgs));
                    if (finalArgs.StartsWith(before, StringComparison.Ordinal))
                    {
                        var delta = finalArgs[before.Length..];
                        if (delta.Length > 0)
                            stream.Push(new ToolCallDeltaEvent(state.ContentIndex, delta, BuildPartial()));
                    }
                    continue;
                }

                if (evt.Event is "response.output_item.done")
                {
                    if (!root.TryGetProperty("item", out var item)) continue;
                    var itemType = GetString(item, "type");
                    var itemId = GetString(item, "id");

                    switch (itemType)
                    {
                        case "reasoning" when itemId is not null && thinkingStates.TryGetValue(itemId, out var thinkingState):
                            contentBlocks[thinkingState.ContentIndex] = new ThinkingContent(
                                thinkingState.Text.ToString(),
                                JsonSerializer.Serialize(item));
                            stream.Push(new ThinkingEndEvent(thinkingState.ContentIndex, thinkingState.Text.ToString(), BuildPartial()));
                            thinkingStates.Remove(itemId);
                            break;

                        case "message" when itemId is not null && textStates.TryGetValue(itemId, out var textState):
                            var phase = GetString(item, "phase");
                            contentBlocks[textState.ContentIndex] = new TextContent(
                                textState.Text.ToString(),
                                EncodeTextSignatureV1(itemId, phase));
                            stream.Push(new TextEndEvent(textState.ContentIndex, textState.Text.ToString(), BuildPartial()));
                            textStates.Remove(itemId);
                            break;

                        case "function_call":
                        {
                            var callId = GetString(item, "call_id");
                            var name = GetString(item, "name") ?? "";
                            var args = GetString(item, "arguments") ?? "";
                            if (callId is null || !toolStates.TryGetValue(callId, out var state)) break;
                            if (args.Length > 0)
                            {
                                state.Arguments.Clear();
                                state.Arguments.Append(args);
                            }

                            var toolCall = new ToolCallContent(
                                ResponsesStreamHelpers.ComposeToolCallId(callId, state.ItemId),
                                name.Length > 0 ? name : state.Name,
                                StreamingJsonParser.Parse(state.Arguments.ToString()));
                            contentBlocks[state.ContentIndex] = toolCall;
                            stream.Push(new ToolCallEndEvent(state.ContentIndex, toolCall, BuildPartial()));
                            toolStates.Remove(callId);
                            break;
                        }
                    }

                    continue;
                }

                if (evt.Event is "response.completed" or "response.done")
                {
                    var responseEl = root.TryGetProperty("response", out var resp) ? resp : root;
                    responseId = GetString(responseEl, "id") ?? responseId;
                    stopReason = ResponsesStreamHelpers.MapStopReason(GetString(responseEl, "status"));

                    if (responseEl.TryGetProperty("incomplete_details", out var incompleteDetails) &&
                        incompleteDetails.ValueKind == JsonValueKind.Object &&
                        string.Equals(GetString(incompleteDetails, "reason"), "content_filter", StringComparison.OrdinalIgnoreCase))
                    {
                        stopReason = StopReason.Sensitive;
                    }
                    else if (sawRefusal && stopReason == StopReason.Stop)
                    {
                        stopReason = StopReason.Refusal;
                    }

                    if (responseEl.TryGetProperty("usage", out var usageEl) &&
                        usageEl.ValueKind == JsonValueKind.Object)
                    {
                        usage = ResponsesStreamHelpers.ParseUsage(usageEl, model);
                        var configuredTier = options is CopilotResponsesOptions ro ? ro.ServiceTier : null;
                        var responseTier = GetString(responseEl, "service_tier");
                        usage = ApplyServiceTierPricing(usage, responseTier ?? configuredTier);
                    }

                    if (contentBlocks.OfType<ToolCallContent>().Any() && stopReason == StopReason.Stop)
                        stopReason = StopReason.ToolUse;

                    break;
                }

                if (evt.Event is "response.failed")
                {
                    var message = GetErrorMessage(root);
                    emitError(stream, model, message, contentBlocks);
                    return;
                }
            }
        }

        var final = BuildPartial() with { StopReason = stopReason };
        stream.Push(new DoneEvent(stopReason, final));
        stream.End(final);
    }

    private static async Task<SseEvent?> ReadSseEventAsync(StreamReader reader, CancellationToken ct)
    {
        string? eventType = null;
        var data = new StringBuilder();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                if (eventType is null && data.Length == 0) return null;
                break;
            }

            if (line.Length == 0)
            {
                if (eventType is not null || data.Length > 0) break;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line[7..];
                continue;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[6..]);
            }
        }

        if (data.Length == 0 || data.ToString() == "[DONE]") return null;
        return new SseEvent(eventType ?? "message", data.ToString());
    }

    private static string GetErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response) &&
            response.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            var code = GetString(error, "code");
            var message = GetString(error, "message");
            return $"{code ?? "unknown"}: {message ?? "no message"}";
        }

        if (root.TryGetProperty("response", out response) &&
            response.TryGetProperty("incomplete_details", out var details) &&
            details.TryGetProperty("reason", out var reason))
        {
            return $"incomplete: {reason.GetString()}";
        }

        if (root.TryGetProperty("message", out var messageEl))
            return messageEl.GetString() ?? "Unknown error";

        return "Unknown error";
    }

    private static string EncodeTextSignatureV1(string id, string? phase)
    {
        var payload = new JsonObject
        {
            ["v"] = 1,
            ["id"] = id
        };
        if (phase is "commentary" or "final_answer")
            payload["phase"] = phase;
        return payload.ToJsonString();
    }

    private static Usage ApplyServiceTierPricing(Usage usage, string? serviceTier)
    {
        var multiplier = serviceTier switch
        {
            "flex" => 0.5m,
            "priority" => 2m,
            _ => 1m
        };
        if (multiplier == 1m)
            return usage;

        var cost = usage.Cost with
        {
            Input = usage.Cost.Input * multiplier,
            Output = usage.Cost.Output * multiplier,
            CacheRead = usage.Cost.CacheRead * multiplier,
            CacheWrite = usage.Cost.CacheWrite * multiplier
        };
        cost = cost with
        {
            Total = cost.Input + cost.Output + cost.CacheRead + cost.CacheWrite
        };

        return usage with { Cost = cost };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

}
