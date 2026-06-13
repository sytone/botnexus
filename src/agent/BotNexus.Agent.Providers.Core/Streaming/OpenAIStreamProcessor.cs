using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Represents open aistream processor.
/// </summary>
public sealed class OpenAIStreamProcessor
{
    /// <summary>
    /// Executes parse open ai completions async.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="reader">The reader.</param>
    /// <param name="model">The model.</param>
    /// <param name="api">The api.</param>
    /// <param name="parseUsage">The parse usage.</param>
    /// <param name="mapStopReason">The map stop reason.</param>
    /// <param name="extractProviderErrorMessage">The extract provider error message.</param>
    /// <param name="emitError">The emit error.</param>
    /// <param name="onMalformedChunk">The on malformed chunk.</param>
    /// <param name="inspectChunk">
    /// Optional per-chunk callback receiving the root JSON element of every
    /// SSE <c>data:</c> payload. Used by provider-specific transports to
    /// surface fields beyond the OpenAI shape (e.g. Copilot's
    /// <c>copilot_usage</c>) without coupling Core to those providers.
    /// </param>
    /// <returns>The parse open ai completions async result.</returns>
    public async Task ParseOpenAiCompletionsAsync(
        LlmStream stream,
        StreamReader reader,
        LlmModel model,
        string api,
        Func<JsonElement, Usage, LlmModel, Usage> parseUsage,
        Func<string?, (StopReason StopReason, string? ErrorMessage)> mapStopReason,
        Func<string, LlmModel, string> extractProviderErrorMessage,
        Action<LlmStream, LlmModel, string, List<ContentBlock>?> emitError,
        Action? onMalformedChunk,
        CancellationToken ct,
        Action<JsonElement>? inspectChunk = null)
    {
        var contentBlocks = new PartialContentTracker();
        var usage = Usage.Empty();
        string? responseId = null;

        var currentTextIndex = -1;
        var currentThinkingIndex = -1;
        string? currentThinkingSignature = null;
        var textAccumulator = new StringBuilder();
        var thinkingAccumulator = new StringBuilder();

        var toolCallState = new Dictionary<int, (string Id, string Name, StringBuilder Args, int ContentIndex, string? ThoughtSignature, Dictionary<string, object?>? LastParsedArgs, int LastParsedLength)>();

        var startEmitted = false;
        StopReason? stopReason = null;
        string? errorMessage = null;

        // BuildPartial is invoked once per streamed event (start, every text/thinking/tool-call
        // delta, and the end events). The previous implementation copied the whole content list
        // (`contentBlocks.ToList()`) on every call, allocating a fresh list plus AssistantMessage
        // record per token-ish event — linear GC pressure on the hottest path in the agent loop.
        // PartialContentTracker caches an immutable snapshot and only rebuilds it when the content
        // list actually changes shape, so back-to-back BuildPartial calls between mutations reuse
        // the same snapshot list. ContentBlock is an immutable record, so a shape-stable snapshot
        // is safe to share across events. (#1378)
        AssistantMessage BuildPartial() => new(
            Content: contentBlocks.Snapshot(),
            Api: api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: usage,
            StopReason: stopReason ?? StopReason.Stop,
            ErrorMessage: errorMessage,
            ResponseId: responseId,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                onMalformedChunk?.Invoke();
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                inspectChunk?.Invoke(root);

                if (root.TryGetProperty("error", out _))
                {
                    var errorMsg = extractProviderErrorMessage(root.GetRawText(), model);
                    emitError(stream, model, errorMsg, contentBlocks.ToList());
                    return;
                }

                if (responseId is null && root.TryGetProperty("id", out var idProp))
                    responseId = idProp.GetString();

                if (root.TryGetProperty("usage", out var usageProp) &&
                    usageProp.ValueKind == JsonValueKind.Object)
                {
                    usage = parseUsage(usageProp, usage, model);
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var choice in choices.EnumerateArray())
                {
                    if (!root.TryGetProperty("usage", out _) &&
                        choice.TryGetProperty("usage", out var choiceUsageProp) &&
                        choiceUsageProp.ValueKind == JsonValueKind.Object)
                    {
                        usage = parseUsage(choiceUsageProp, usage, model);
                    }

                    if (choice.TryGetProperty("finish_reason", out var finishProp) &&
                        finishProp.ValueKind == JsonValueKind.String)
                    {
                        var mapped = mapStopReason(finishProp.GetString());
                        stopReason = mapped.StopReason;
                        if (!string.IsNullOrWhiteSpace(mapped.ErrorMessage))
                            errorMessage = mapped.ErrorMessage;
                    }

                    if (!choice.TryGetProperty("delta", out var delta))
                        continue;

                    if (delta.TryGetProperty("refusal", out var refusalProp) &&
                        refusalProp.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(refusalProp.GetString()))
                    {
                        stopReason = StopReason.Refusal;
                    }

                    if (!startEmitted)
                    {
                        stream.Push(new StartEvent(BuildPartial()));
                        startEmitted = true;
                    }

                    string? reasoningField = null;
                    if (delta.TryGetProperty("reasoning_content", out var reasoningContentProp) &&
                        reasoningContentProp.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(reasoningContentProp.GetString()))
                    {
                        reasoningField = "reasoning_content";
                    }
                    else if (delta.TryGetProperty("reasoning", out var reasoningProp) &&
                             reasoningProp.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrEmpty(reasoningProp.GetString()))
                    {
                        reasoningField = "reasoning";
                    }
                    else if (delta.TryGetProperty("reasoning_text", out var reasoningTextProp) &&
                             reasoningTextProp.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrEmpty(reasoningTextProp.GetString()))
                    {
                        reasoningField = "reasoning_text";
                    }

                    if (reasoningField is not null)
                    {
                        var thinking = delta.GetProperty(reasoningField).GetString() ?? "";
                        if (thinking.Length > 0)
                        {
                            if (currentThinkingIndex < 0)
                            {
                                currentThinkingIndex = contentBlocks.Count;
                                currentThinkingSignature = reasoningField;
                                contentBlocks.Add(new ThinkingContent(string.Empty, currentThinkingSignature));
                                stream.Push(new ThinkingStartEvent(currentThinkingIndex, BuildPartial()));
                            }

                            thinkingAccumulator.Append(thinking);
                            contentBlocks[currentThinkingIndex] = new ThinkingContent(
                                thinkingAccumulator.ToString(),
                                currentThinkingSignature);
                            stream.Push(new ThinkingDeltaEvent(currentThinkingIndex, thinking, BuildPartial()));
                        }
                    }

                    if (delta.TryGetProperty("content", out var contentProp) &&
                        contentProp.ValueKind == JsonValueKind.String)
                    {
                        var text = contentProp.GetString() ?? "";
                        if (text.Length > 0)
                        {
                            if (currentThinkingIndex >= 0)
                            {
                                stream.Push(new ThinkingEndEvent(
                                    currentThinkingIndex,
                                    thinkingAccumulator.ToString(),
                                    BuildPartial()));
                                currentThinkingIndex = -1;
                                currentThinkingSignature = null;
                            }

                            if (currentTextIndex < 0)
                            {
                                currentTextIndex = contentBlocks.Count;
                                contentBlocks.Add(new TextContent(""));
                                stream.Push(new TextStartEvent(currentTextIndex, BuildPartial()));
                            }

                            textAccumulator.Append(text);
                            contentBlocks[currentTextIndex] = new TextContent(textAccumulator.ToString());
                            stream.Push(new TextDeltaEvent(currentTextIndex, text, BuildPartial()));
                        }
                    }

                    var reasoningDetailsByToolId = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (delta.TryGetProperty("reasoning_details", out var reasoningDetailsProp) &&
                        reasoningDetailsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var detail in reasoningDetailsProp.EnumerateArray())
                        {
                            if (detail.ValueKind != JsonValueKind.Object ||
                                !detail.TryGetProperty("id", out var detailIdProp))
                            {
                                continue;
                            }

                            var detailId = detailIdProp.GetString();
                            if (!string.IsNullOrWhiteSpace(detailId))
                                reasoningDetailsByToolId[detailId] = detail.GetRawText();
                        }
                    }

                    if (delta.TryGetProperty("tool_calls", out var tcProp) &&
                        tcProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcProp.EnumerateArray())
                        {
                            var tcIndex = tc.TryGetProperty("index", out var idxProp)
                                ? idxProp.GetInt32()
                                : 0;
                            string? thoughtSignature = null;
                            var tcId = tc.TryGetProperty("id", out var tcIdProp)
                                ? tcIdProp.GetString() ?? string.Empty
                                : string.Empty;
                            if (!string.IsNullOrWhiteSpace(tcId) &&
                                reasoningDetailsByToolId.TryGetValue(tcId, out var detailSignature))
                            {
                                thoughtSignature = detailSignature;
                            }
                            else if (toolCallState.TryGetValue(tcIndex, out var existingState) &&
                                     !string.IsNullOrWhiteSpace(existingState.Id) &&
                                     reasoningDetailsByToolId.TryGetValue(existingState.Id, out var existingSignature))
                            {
                                thoughtSignature = existingSignature;
                            }

                            if (!toolCallState.ContainsKey(tcIndex))
                            {
                                CloseOpenBlocks(
                                    stream, ref currentTextIndex, ref currentThinkingIndex,
                                    ref currentThinkingSignature,
                                    textAccumulator, thinkingAccumulator, BuildPartial);

                                var fnName = "";
                                if (tc.TryGetProperty("function", out var fnProp) &&
                                    fnProp.TryGetProperty("name", out var nameProp))
                                    fnName = nameProp.GetString() ?? "";

                                var contentIndex = contentBlocks.Count;
                                contentBlocks.Add(new ToolCallContent(tcId, fnName, []));
                                toolCallState[tcIndex] = (tcId, fnName, new StringBuilder(), contentIndex, thoughtSignature, null, -1);

                                stream.Push(new ToolCallStartEvent(contentIndex, BuildPartial()));
                            }

                            if (tc.TryGetProperty("function", out var fnDeltaProp) &&
                                fnDeltaProp.TryGetProperty("arguments", out var argsProp) &&
                                argsProp.ValueKind == JsonValueKind.String)
                            {
                                var argsChunk = argsProp.GetString() ?? "";
                                if (argsChunk.Length > 0)
                                {
                                    var state = toolCallState[tcIndex];
                                    state.Args.Append(argsChunk);
                                    if (thoughtSignature is not null)
                                        state.ThoughtSignature = thoughtSignature;

                                    // Per-delta parse is required: consumers (StreamAccumulator)
                                    // read the parsed args off the partial message on every delta,
                                    // so deferring the parse to the end would change observable
                                    // behavior. We do cache the result + the buffer length it was
                                    // parsed at, so the closing pass below does not re-parse an
                                    // unchanged buffer (the redundant final parse this used to do).
                                    var bufferLength = state.Args.Length;
                                    Dictionary<string, object?> parsedArgs;
                                    if (state.LastParsedArgs is not null && state.LastParsedLength == bufferLength)
                                    {
                                        parsedArgs = state.LastParsedArgs;
                                    }
                                    else
                                    {
                                        parsedArgs = StreamingJsonParser.Parse(state.Args.ToString());
                                        state.LastParsedArgs = parsedArgs;
                                        state.LastParsedLength = bufferLength;
                                    }
                                    toolCallState[tcIndex] = state;

                                    contentBlocks[state.ContentIndex] =
                                        new ToolCallContent(state.Id, state.Name, parsedArgs, state.ThoughtSignature);

                                    stream.Push(new ToolCallDeltaEvent(
                                        state.ContentIndex, argsChunk, BuildPartial()));
                                }
                            }
                        }
                    }
                }
            }
        }

        if (currentThinkingIndex >= 0)
            stream.Push(new ThinkingEndEvent(currentThinkingIndex, thinkingAccumulator.ToString(), BuildPartial()));

        if (currentTextIndex >= 0)
            stream.Push(new TextEndEvent(currentTextIndex, textAccumulator.ToString(), BuildPartial()));

        foreach (var (_, state) in toolCallState)
        {
            // Reuse the args parsed on the final delta when the buffer has not grown since
            // (the common case — a tool call's last delta parses the complete buffer). Only
            // re-parse if no cached parse covers the current buffer length, e.g. a tool call
            // that received an id/name but never any argument deltas. (#1378)
            var parsedArgs = state.LastParsedArgs is not null && state.LastParsedLength == state.Args.Length
                ? state.LastParsedArgs
                : StreamingJsonParser.Parse(state.Args.ToString());
            var toolCall = new ToolCallContent(state.Id, state.Name, parsedArgs, state.ThoughtSignature);
            contentBlocks[state.ContentIndex] = toolCall;
            stream.Push(new ToolCallEndEvent(state.ContentIndex, toolCall, BuildPartial()));
        }

        var finalMessage = BuildPartial() with
        {
            StopReason = stopReason ?? StopReason.Stop,
            ErrorMessage = errorMessage
        };
        stream.Push(new DoneEvent(stopReason ?? StopReason.Stop, finalMessage));
        stream.End(finalMessage);
    }

    /// <summary>
    /// Executes parse compat async.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="reader">The reader.</param>
    /// <param name="model">The model.</param>
    /// <param name="api">The api.</param>
    /// <param name="parseUsage">The parse usage.</param>
    /// <param name="mapStopReason">The map stop reason.</param>
    /// <returns>The parse compat async result.</returns>
    public async Task ParseCompatAsync(
        LlmStream stream,
        StreamReader reader,
        LlmModel model,
        string api,
        Func<JsonElement, Usage, LlmModel, Usage> parseUsage,
        Func<string?, bool, (StopReason StopReason, string? ErrorMessage)> mapStopReason,
        CancellationToken ct)
    {
        var output = CreatePartialMessage(model, api);
        var contentBuilder = new StringBuilder();
        var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
        var contentIndex = 0;
        var started = false;
        string? responseId = null;
        string? finishReason = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line[6..];

            if (data == "[DONE]")
                break;

            JsonElement chunk;
            try
            {
                using var parsed = JsonDocument.Parse(data);
                chunk = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            responseId ??= chunk.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            if (chunk.TryGetProperty("usage", out var usageProp))
                output = output with { Usage = parseUsage(usageProp, output.Usage, model) };

            if (!chunk.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out var frProp) && frProp.ValueKind != JsonValueKind.Null)
                finishReason = frProp.GetString();

            if (!choice.TryGetProperty("delta", out var delta))
                continue;

            if (!started)
            {
                stream.Push(new StartEvent(output));
                started = true;
            }

            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var text = contentProp.GetString() ?? "";
                if (text.Length > 0)
                {
                    var sanitized = UnicodeSanitizer.SanitizeSurrogates(text);

                    if (contentBuilder.Length == 0)
                        stream.Push(new TextStartEvent(contentIndex, output));

                    contentBuilder.Append(sanitized);
                    stream.Push(new TextDeltaEvent(contentIndex, sanitized, output));
                }
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCallsProp.EnumerateArray())
                {
                    var tcIndex = tc.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : 0;

                    if (!toolCallBuilders.TryGetValue(tcIndex, out var builder))
                    {
                        if (contentBuilder.Length > 0)
                        {
                            stream.Push(new TextEndEvent(contentIndex, contentBuilder.ToString(), output));
                            contentIndex++;
                        }

                        builder = new ToolCallBuilder();
                        toolCallBuilders[tcIndex] = builder;

                        if (tc.TryGetProperty("id", out var tcId))
                            builder.Id = tcId.GetString() ?? "";
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var nameProp))
                                builder.Name = nameProp.GetString() ?? "";
                        }

                        stream.Push(new ToolCallStartEvent(contentIndex + tcIndex, output));
                    }

                    if (tc.TryGetProperty("function", out var fnDelta))
                    {
                        if (fnDelta.TryGetProperty("name", out var nameDelta))
                            builder.Name ??= nameDelta.GetString() ?? "";

                        if (fnDelta.TryGetProperty("arguments", out var argsDelta))
                        {
                            var argChunk = argsDelta.GetString() ?? "";
                            builder.ArgumentsJson.Append(argChunk);
                            stream.Push(new ToolCallDeltaEvent(contentIndex + tcIndex, argChunk, output));
                        }
                    }
                }
            }
        }

        if (contentBuilder.Length > 0 && toolCallBuilders.Count == 0)
            stream.Push(new TextEndEvent(contentIndex, contentBuilder.ToString(), output));

        foreach (var (tcIndex, builder) in toolCallBuilders)
        {
            var args = StreamingJsonParser.Parse(builder.ArgumentsJson.ToString());
            var toolCall = new ToolCallContent(builder.Id, builder.Name ?? "", args);
            stream.Push(new ToolCallEndEvent(contentIndex + tcIndex, toolCall, output));
        }

        var mappedStop = mapStopReason(finishReason, toolCallBuilders.Count > 0);

        var finalContent = BuildFinalContent(contentBuilder, toolCallBuilders);
        var finalMessage = new AssistantMessage(
            Content: finalContent,
            Api: api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: output.Usage,
            StopReason: mappedStop.StopReason,
            ErrorMessage: mappedStop.ErrorMessage,
            ResponseId: responseId,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        stream.Push(new DoneEvent(mappedStop.StopReason, finalMessage));
        stream.End(finalMessage);
    }

    private static IReadOnlyList<ContentBlock> BuildFinalContent(
        StringBuilder contentBuilder, Dictionary<int, ToolCallBuilder> toolCallBuilders)
    {
        var blocks = new List<ContentBlock>();
        if (contentBuilder.Length > 0)
            blocks.Add(new TextContent(contentBuilder.ToString()));
        foreach (var (_, builder) in toolCallBuilders.OrderBy(kvp => kvp.Key))
        {
            var args = StreamingJsonParser.Parse(builder.ArgumentsJson.ToString());
            blocks.Add(new ToolCallContent(builder.Id, builder.Name ?? "", args));
        }
        return blocks;
    }

    private static AssistantMessage CreatePartialMessage(LlmModel model, string api)
    {
        return new AssistantMessage(
            Content: [],
            Api: api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }

    private static void CloseOpenBlocks(
        LlmStream stream,
        ref int currentTextIndex,
        ref int currentThinkingIndex,
        ref string? currentThinkingSignature,
        StringBuilder textAccumulator,
        StringBuilder thinkingAccumulator,
        Func<AssistantMessage> buildPartial)
    {
        if (currentThinkingIndex >= 0)
        {
            stream.Push(new ThinkingEndEvent(currentThinkingIndex, thinkingAccumulator.ToString(), buildPartial()));
            currentThinkingIndex = -1;
            currentThinkingSignature = null;
        }

        if (currentTextIndex >= 0)
        {
            stream.Push(new TextEndEvent(currentTextIndex, textAccumulator.ToString(), buildPartial()));
            currentTextIndex = -1;
        }
    }

    private sealed class ToolCallBuilder
    {
        /// <summary>
        /// Gets or sets the id.
        /// </summary>
        public string Id { get; set; } = "";
        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Gets the arguments json.
        /// </summary>
        public StringBuilder ArgumentsJson { get; } = new();
    }

    /// <summary>
    /// Mutable content-block list with a cached immutable snapshot for the streaming partial
    /// message. <c>BuildPartial()</c> runs once per streamed event; copying the whole list each
    /// time was a linear allocation source on the hottest path. This tracker rebuilds the
    /// snapshot only when the list actually changes shape (an <see cref="Add"/> or an indexer
    /// replace), so the many <see cref="Snapshot"/> reads that happen between mutations all
    /// share the same immutable list. <see cref="ContentBlock"/> is an immutable record, so a
    /// shape-stable snapshot is safe to hand out across events. (#1378)
    /// </summary>
    private sealed class PartialContentTracker
    {
        private readonly List<ContentBlock> _blocks = [];
        private IReadOnlyList<ContentBlock>? _snapshot;

        /// <summary>Gets the number of content blocks accumulated so far.</summary>
        public int Count => _blocks.Count;

        /// <summary>Appends a block and invalidates the cached snapshot.</summary>
        public void Add(ContentBlock block)
        {
            _blocks.Add(block);
            _snapshot = null;
        }

        /// <summary>
        /// Replaces the block at <paramref name="index"/> and invalidates the cached snapshot.
        /// Only a setter is exposed because the processor never reads blocks back through the
        /// indexer — it tracks indices in local variables.
        /// </summary>
        public ContentBlock this[int index]
        {
            set
            {
                _blocks[index] = value;
                _snapshot = null;
            }
        }

        /// <summary>
        /// Returns the current content as an immutable snapshot, reusing the cached instance when
        /// the list has not changed shape since the last call.
        /// </summary>
        public IReadOnlyList<ContentBlock> Snapshot() => _snapshot ??= _blocks.ToArray();

        /// <summary>
        /// Returns a fresh mutable copy of the current content. Used only on the cold error path
        /// where a <c>List&lt;ContentBlock&gt;</c> is required by the provider error callback.
        /// </summary>
        public List<ContentBlock> ToList() => [.. _blocks];
    }
}
