using System.Text;
using System.Text.Json;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Utilities;

namespace BotNexus.Providers.Core.Streaming;

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
        CancellationToken ct)
    {
        var contentBlocks = new List<ContentBlock>();
        var usage = Usage.Empty();
        string? responseId = null;

        var currentTextIndex = -1;
        var currentThinkingIndex = -1;
        string? currentThinkingSignature = null;
        var textAccumulator = new StringBuilder();
        var thinkingAccumulator = new StringBuilder();

        var toolCallState = new Dictionary<int, (string Id, string Name, StringBuilder Args, int ContentIndex, string? ThoughtSignature)>();

        var startEmitted = false;
        StopReason? stopReason = null;
        string? errorMessage = null;

        AssistantMessage BuildPartial() => new(
            Content: contentBlocks.ToList(),
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

                if (root.TryGetProperty("error", out _))
                {
                    var errorMsg = extractProviderErrorMessage(root.GetRawText(), model);
                    emitError(stream, model, errorMsg, contentBlocks);
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
                                toolCallState[tcIndex] = (tcId, fnName, new StringBuilder(), contentIndex, thoughtSignature);

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
                                    toolCallState[tcIndex] = state;

                                    var parsedArgs = StreamingJsonParser.Parse(state.Args.ToString());
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
            var parsedArgs = StreamingJsonParser.Parse(state.Args.ToString());
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
}
