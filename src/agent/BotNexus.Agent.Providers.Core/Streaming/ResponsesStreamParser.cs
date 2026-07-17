using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// The single shared Responses API SSE stream parser for every Responses-flavoured provider
/// (OpenAI and Copilot today). Extracted verbatim from the previously-duplicated
/// <c>OpenAIResponsesStreamParser</c> / <c>CopilotResponsesStreamParser</c> (#1545, slice 2 of the
/// #1540 post-#1377 drift cleanup): those two parsers were ~95% byte-identical and differed only in
/// two provider deltas, which are now supplied as delegates so this type stays provider-agnostic:
/// <list type="bullet">
/// <item><paramref name="onParsedEvent"/> -- a per-event hook the Copilot provider uses for usage
/// telemetry (<c>CopilotUsageActivity.TryParseAndEmit</c>); OpenAI passes <c>null</c>.</item>
/// <item><paramref name="resolveConfiguredServiceTier"/> -- reads the configured service tier from the
/// provider's own options type (<c>OpenAIResponsesOptions</c> / <c>CopilotResponsesOptions</c>);
/// either may pass <c>null</c> when no configured tier applies.</item>
/// </list>
/// This is the same delegate-injection seam <see cref="ResponsesTransportProfile"/> already uses for
/// the build/parse/header hooks. Behaviour preservation is guarded by
/// <c>CopilotResponsesProviderParityTests</c> (byte-identical wire contract) and
/// <c>OpenAIResponsesProviderTests</c>; <c>ResponsesStreamParserUnificationTests</c> locks the single
/// Core home.
/// </summary>
public static class ResponsesStreamParser
{
    /// <summary>
    /// Drains a Responses API SSE <paramref name="reader"/> into the <paramref name="stream"/>,
    /// emitting start/delta/end events for text, reasoning, and tool-call content.
    /// </summary>
    /// <param name="stream">The output stream events are pushed to.</param>
    /// <param name="reader">The SSE response body reader.</param>
    /// <param name="model">The model the request was issued against.</param>
    /// <param name="options">The (possibly provider-specific) stream options.</param>
    /// <param name="api">The provider <c>Api</c> identifier surfaced on emitted messages.</param>
    /// <param name="logger">The provider logger (debug-logs malformed SSE events).</param>
    /// <param name="emitError">The provider's error-emit callback.</param>
    /// <param name="onParsedEvent">
    /// Optional per-event hook invoked with each successfully parsed SSE event's JSON root, before
    /// the event is dispatched. The Copilot provider uses it for usage telemetry; OpenAI passes null.
    /// </param>
    /// <param name="resolveConfiguredServiceTier">
    /// Optional resolver returning the configured service tier from <paramref name="options"/> (the
    /// provider supplies the cast to its own options type). Used to price usage on completion when
    /// the response body omits <c>service_tier</c>. May be null.
    /// </param>
    /// <param name="normalizeTextDelta">
    /// Optional transport-compatibility hook applied only to text/refusal delta payloads before
    /// they are accumulated or emitted. Providers should leave this null unless their upstream
    /// transport has a confirmed wire-level quirk; tool arguments and reasoning are never changed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static Task ParseAsync(
        LlmStream stream,
        StreamReader reader,
        LlmModel model,
        StreamOptions? options,
        string api,
        ILogger logger,
        Action<LlmStream, LlmModel, string, IReadOnlyList<ContentBlock>?> emitError,
        Action<JsonElement>? onParsedEvent,
        Func<StreamOptions?, string?>? resolveConfiguredServiceTier,
        Func<LlmModel, string, string>? normalizeTextDelta,
        CancellationToken ct)
        => ParseEventsAsync(
            stream,
            async cancellationToken =>
            {
                var evt = await ReadSseEventAsync(reader, cancellationToken).ConfigureAwait(false);
                return evt is null ? null : new ResponsesEvent(evt.Event, evt.Data);
            },
            model,
            options,
            api,
            logger,
            emitError,
            onParsedEvent,
            resolveConfiguredServiceTier,
            normalizeTextDelta,
            ct);

    /// <summary>
    /// Normalizes Responses JSON events from any provider-private wire transport into the shared
    /// <see cref="LlmStream"/> contract. SSE and WebSocket adapters differ only in how they supply
    /// the next JSON event.
    /// </summary>
    public static async Task ParseEventsAsync(
        LlmStream stream,
        Func<CancellationToken, ValueTask<ResponsesEvent?>> readEvent,
        LlmModel model,
        StreamOptions? options,
        string api,
        ILogger logger,
        Action<LlmStream, LlmModel, string, IReadOnlyList<ContentBlock>?> emitError,
        Action<JsonElement>? onParsedEvent,
        Func<StreamOptions?, string?>? resolveConfiguredServiceTier,
        Func<LlmModel, string, string>? normalizeTextDelta,
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
            var evt = await readEvent(ct).ConfigureAwait(false);
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

                onParsedEvent?.Invoke(root);

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
                            if (!string.IsNullOrWhiteSpace(itemId))
                                toolStates[itemId] = state;

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
                    if (normalizeTextDelta is not null)
                        delta = normalizeTextDelta(model, delta);
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
                    var stateKey = GetString(root, "call_id") ?? GetString(root, "item_id");
                    var delta = GetString(root, "delta") ?? "";
                    if (stateKey is null || delta.Length == 0 || !toolStates.TryGetValue(stateKey, out var state)) continue;

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
                    var stateKey = GetString(root, "call_id") ?? GetString(root, "item_id");
                    var finalArgs = GetString(root, "arguments") ?? "";
                    if (stateKey is null || !toolStates.TryGetValue(stateKey, out var state)) continue;
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
                            if (!string.IsNullOrWhiteSpace(state.ItemId))
                                toolStates.Remove(state.ItemId);
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
                        var configuredTier = resolveConfiguredServiceTier?.Invoke(options);
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
