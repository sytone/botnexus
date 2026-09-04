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
        // Text items whose content arrived on the refusal channel. Membership decides which
        // content-block kind the item is rebuilt into on every later delta and on item.done, so a
        // refusal can never be silently re-labelled as ordinary prose (#3295).
        var refusalItems = new HashSet<string>(StringComparer.Ordinal);

        var textStates = new Dictionary<string, (int ContentIndex, StringBuilder Text)>(StringComparer.Ordinal);
        var thinkingStates = new Dictionary<string, (int ContentIndex, StringBuilder Text)>(StringComparer.Ordinal);
        var toolStates = new Dictionary<string, ToolState>(StringComparer.Ordinal);
        // Per-text-item delta counts, carried solely so the assembly-conformance diagnostic can
        // report how many fragments produced a mismatched buffer (#2443).
        var textDeltaCounts = new Dictionary<string, int>(StringComparer.Ordinal);

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
                // Same non-terminal report as the completions path (#3291): a skipped frame is a
                // silent content loss that only debug logging witnessed. The event name is a
                // protocol discriminator, not content, so it is safe to include.
                stream.Push(new WarningEvent(
                    WarningCodes.MalformedChunkSkipped,
                    $"Skipping malformed responses SSE event '{evt.Event}': the frame could not be " +
                    $"parsed as JSON and was discarded. api={api} model={model.Id} " +
                    $"provider={model.Provider}. Content for this frame is lost; the turn continues.",
                    BuildPartial()));
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
                            // The id carried on the event is the same composed id the end event and the
                            // content block use, so a consumer can correlate start -> delta -> end with a
                            // single key rather than re-deriving it (#3290).
                            var composedId = ResponsesStreamHelpers.ComposeToolCallId(callId, itemId);
                            contentBlocks.Add(new ToolCallContent(composedId, name, parsed));
                            stream.Push(new ToolCallStartEvent(index, BuildPartial(), composedId, name));

                            var state = new ToolState(callId, itemId, name, index);
                            state.Arguments.Append(arguments);
                            toolStates[callId] = state;
                            if (!string.IsNullOrWhiteSpace(itemId))
                                toolStates[itemId] = state;

                            if (arguments.Length > 0)
                                stream.Push(new ToolCallDeltaEvent(index, arguments, BuildPartial(), composedId, name));
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
                    // Refusal is tracked per text item, not just per response: the block this
                    // item's content is rebuilt into must stay a RefusalContent for every
                    // subsequent delta, otherwise the second fragment would silently demote the
                    // block back to ordinary prose (#3295).
                    var isRefusal = evt.Event is "response.refusal.delta";
                    if (isRefusal)
                    {
                        sawRefusal = true;
                        stopReason = StopReason.Refusal;
                    }
                    var itemId = GetString(root, "item_id");
                    var delta = GetString(root, "delta") ?? "";
                    if (normalizeTextDelta is not null)
                        delta = normalizeTextDelta(model, delta);
                    if (delta.Length == 0) continue;

                    var stateKeyForRefusal = itemId ?? Guid.NewGuid().ToString("N");
                    if (itemId is null || !textStates.TryGetValue(itemId, out var state))
                    {
                        var index = contentBlocks.Count;
                        contentBlocks.Add(isRefusal ? new RefusalContent("") : new TextContent(""));
                        state = (index, new StringBuilder());
                        textStates[stateKeyForRefusal] = state;
                        stream.Push(new TextStartEvent(index, BuildPartial()));
                    }

                    if (isRefusal)
                        refusalItems.Add(stateKeyForRefusal);

                    state.Text.Append(delta);
                    contentBlocks[state.ContentIndex] = refusalItems.Contains(stateKeyForRefusal)
                        ? new RefusalContent(state.Text.ToString())
                        : new TextContent(state.Text.ToString());
                    stream.Push(new TextDeltaEvent(state.ContentIndex, delta, BuildPartial()));
                    if (itemId is not null)
                    {
                        textStates[itemId] = state;
                        textDeltaCounts[itemId] = textDeltaCounts.GetValueOrDefault(itemId) + 1;
                    }
                    continue;
                }

                if (evt.Event is "response.function_call_arguments.delta")
                {
                    EnsureStart();
                    var stateKey = GetString(root, "call_id") ?? GetString(root, "item_id");
                    var delta = GetString(root, "delta") ?? "";
                    if (stateKey is null || delta.Length == 0 || !toolStates.TryGetValue(stateKey, out var state)) continue;

                    state.Arguments.Append(delta);
                    var deltaToolId = ResponsesStreamHelpers.ComposeToolCallId(state.CallId, state.ItemId);
                    contentBlocks[state.ContentIndex] = new ToolCallContent(
                        deltaToolId,
                        state.Name,
                        StreamingJsonParser.Parse(state.Arguments.ToString()));
                    stream.Push(new ToolCallDeltaEvent(
                        state.ContentIndex, delta, BuildPartial(), deltaToolId, state.Name));
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
                    var doneToolId = ResponsesStreamHelpers.ComposeToolCallId(state.CallId, state.ItemId);
                    contentBlocks[state.ContentIndex] = new ToolCallContent(
                        doneToolId,
                        state.Name,
                        StreamingJsonParser.Parse(finalArgs));
                    if (finalArgs.StartsWith(before, StringComparison.Ordinal))
                    {
                        var delta = finalArgs[before.Length..];
                        if (delta.Length > 0)
                            stream.Push(new ToolCallDeltaEvent(
                                state.ContentIndex, delta, BuildPartial(), doneToolId, state.Name));
                    }
                    continue;
                }

                // The provider's own authoritative text for the block. Comparing it against what we
                // accumulated is a free per-response checksum that we previously discarded (#2443):
                // it is what turns a silent assembly defect into a named, self-reporting event
                // instead of a multi-issue archaeology dig across transports.
                if (evt.Event is "response.output_text.done")
                {
                    var itemId = GetString(root, "item_id");
                    var finalText = GetString(root, "text");
                    if (itemId is null || finalText is null || !textStates.TryGetValue(itemId, out var doneState))
                        continue;

                    var assembled = doneState.Text.ToString();
                    var canonical = StreamAssemblyConformance.Reconcile(
                        assembled,
                        finalText,
                        model.Provider,
                        model.Id,
                        api,
                        "responses",
                        textDeltaCounts.GetValueOrDefault(itemId),
                        logger,
                        stream,
                        BuildPartial);

                    if (!ReferenceEquals(canonical, assembled))
                    {
                        doneState.Text.Clear();
                        doneState.Text.Append(canonical);
                        contentBlocks[doneState.ContentIndex] = refusalItems.Contains(itemId)
                            ? new RefusalContent(canonical)
                            : new TextContent(canonical);
                        textStates[itemId] = doneState;
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
                            // A refusal item must close as a RefusalContent. Rebuilding it as a
                            // plain TextContent here would undo the classification at the very
                            // last event, which is precisely the silent demotion #3295 is about.
                            contentBlocks[textState.ContentIndex] = refusalItems.Contains(itemId)
                                ? new RefusalContent(textState.Text.ToString())
                                : new TextContent(
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

    /// <summary>
    /// Reads a named property only when <paramref name="element"/> is actually a JSON object.
    /// <c>JsonElement.TryGetProperty</c> is <em>partial</em>, not total: on any non-object kind it
    /// throws <see cref="InvalidOperationException"/> rather than returning <c>false</c>. Routing
    /// every provider-payload property access through this helper means a property access added
    /// later inherits the kind check instead of re-introducing #3130, where a
    /// <c>{"response": null}</c> failure event crashed the error-reporting path itself and replaced
    /// the upstream API error with a parser stack trace.
    /// </summary>
    private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value);
    }

    /// <summary>
    /// Best-effort description of a failure event. Provider payloads are untrusted input: a shape
    /// the parser did not anticipate must degrade to a worse message, never to an exception (#3130).
    /// </summary>
    internal static string GetErrorMessage(JsonElement root)
    {
        if (TryGetObjectProperty(root, "response", out var response) &&
            TryGetObjectProperty(response, "error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            var code = GetString(error, "code");
            var message = GetString(error, "message");
            return $"{code ?? "unknown"}: {message ?? "no message"}";
        }

        if (TryGetObjectProperty(root, "response", out response) &&
            TryGetObjectProperty(response, "incomplete_details", out var details) &&
            GetString(details, "reason") is { } reason)
        {
            return $"incomplete: {reason}";
        }

        if (TryGetObjectProperty(root, "message", out var messageEl))
            return (messageEl.ValueKind == JsonValueKind.String ? messageEl.GetString() : null) ?? "Unknown error";

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
        if (TryGetObjectProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }
}
