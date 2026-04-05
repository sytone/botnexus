using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Compatibility;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using BotNexus.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.OpenAI;

/// <summary>
/// OpenAI Chat Completions API provider.
/// Port of pi-mono's providers/openai-completions.ts.
/// Uses raw HttpClient for SSE streaming — full control over headers, compat, and streaming.
/// </summary>
public sealed class OpenAICompletionsProvider(
    HttpClient httpClient,
    ILogger<OpenAICompletionsProvider> logger) : IApiProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public string Api => "openai-completions";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamCoreAsync(stream, model, context, options, ct);
            }
            catch (OperationCanceledException)
            {
                EmitAborted(stream, model);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OpenAI completions stream failed for model {Model}", model.Id);
                EmitError(stream, model, ex.Message);
            }
        }, ct);

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";

        var completionsOptions = new OpenAICompletionsOptions
        {
            ApiKey = apiKey,
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxTokens,
            CancellationToken = options?.CancellationToken ?? CancellationToken.None,
            Transport = options?.Transport ?? Transport.Sse,
            CacheRetention = options?.CacheRetention ?? CacheRetention.Short,
            SessionId = options?.SessionId,
            OnPayload = options?.OnPayload,
            Headers = options?.Headers,
            MaxRetryDelayMs = options?.MaxRetryDelayMs ?? 60000,
            Metadata = options?.Metadata,
        };

        if (options?.Reasoning is not null && model.Reasoning)
            completionsOptions.ReasoningEffort = MapThinkingLevel(options.Reasoning.Value, model.Compat);

        return Stream(model, context, completionsOptions);
    }

    private async Task StreamCoreAsync(
        LlmStream stream,
        LlmModel model,
        Context context,
        StreamOptions? options,
        CancellationToken ct)
    {
        var compat = model.Compat ?? new OpenAICompletionsCompat();
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }

        var messages = MessageTransformer.TransformMessages(context.Messages, model);

        var payload = BuildRequestPayload(model, context.SystemPrompt, messages, context.Tools, options, compat);

        if (options?.OnPayload is not null)
        {
            var modified = await options.OnPayload(payload, model);
            if (modified is JsonObject obj)
                payload = obj;
        }

        var url = $"{model.BaseUrl.TrimEnd('/')}/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (options?.Headers is not null)
        {
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
        {
            var hasImages = CopilotHeaders.HasVisionInput(messages);
            foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, hasImages))
                request.Headers.TryAddWithoutValidation(key, value);
        }

        logger.LogDebug("Streaming {Model} from {Url}", model.Id, url);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        await ParseSseStream(stream, reader, model, compat, ct);
    }

    #region Payload Building

    private static JsonObject BuildRequestPayload(
        LlmModel model,
        string? systemPrompt,
        IReadOnlyList<Message> messages,
        IReadOnlyList<Tool>? tools,
        StreamOptions? options,
        OpenAICompletionsCompat compat)
    {
        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = true,
        };

        if (options?.MaxTokens is not null)
            payload[compat.MaxTokensField] = options.MaxTokens.Value;

        if (options?.Temperature is not null)
            payload["temperature"] = options.Temperature.Value;

        if (compat.SupportsStore)
            payload["store"] = false;

        if (compat.SupportsUsageInStreaming)
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };

        // Reasoning / thinking support
        if (options is OpenAICompletionsOptions { ReasoningEffort: not null } compOptions)
        {
            if (compat.ThinkingFormat is "openai")
            {
                payload["reasoning_effort"] = compOptions.ReasoningEffort;
            }
            else
            {
                // zAI / qwen / openrouter style
                payload["enable_thinking"] = true;
                payload["thinking_format"] = compat.ThinkingFormat;
            }
        }

        if (options is OpenAICompletionsOptions { ToolChoice: not null } tcOptions)
            payload["tool_choice"] = tcOptions.ToolChoice;

        payload["messages"] = ConvertMessages(systemPrompt, messages, compat);

        if (tools is { Count: > 0 })
            payload["tools"] = ConvertTools(tools, compat);

        return payload;
    }

    #endregion

    #region Message Conversion

    private static JsonArray ConvertMessages(
        string? systemPrompt,
        IReadOnlyList<Message> messages,
        OpenAICompletionsCompat compat)
    {
        var result = new JsonArray();

        if (systemPrompt is not null)
        {
            var role = compat.SupportsDeveloperRole ? "developer" : "system";
            result.Add(new JsonObject { ["role"] = role, ["content"] = systemPrompt });
        }

        for (var i = 0; i < messages.Count; i++)
        {
            switch (messages[i])
            {
                case UserMessage user:
                    result.Add(ConvertUserMessage(user));
                    break;

                case AssistantMessage assistant:
                    result.Add(ConvertAssistantMessage(assistant, compat));
                    break;

                case ToolResultMessage toolResult:
                    result.Add(ConvertToolResultMessage(toolResult, compat));
                    if (compat.RequiresAssistantAfterToolResult
                        && i + 1 < messages.Count
                        && messages[i + 1] is UserMessage)
                    {
                        result.Add(new JsonObject { ["role"] = "assistant", ["content"] = "" });
                    }
                    break;
            }
        }

        return result;
    }

    private static JsonObject ConvertUserMessage(UserMessage user)
    {
        if (user.Content.IsText)
            return new JsonObject { ["role"] = "user", ["content"] = user.Content.Text };

        var contentArray = new JsonArray();
        foreach (var block in user.Content.Blocks!)
        {
            switch (block)
            {
                case TextContent text:
                    contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;

                case ImageContent image:
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = $"data:{image.MimeType};base64,{image.Data}"
                        }
                    });
                    break;
            }
        }

        return new JsonObject { ["role"] = "user", ["content"] = contentArray };
    }

    private static JsonObject ConvertAssistantMessage(AssistantMessage assistant, OpenAICompletionsCompat compat)
    {
        var msg = new JsonObject { ["role"] = "assistant" };
        var textParts = new List<string>();
        var toolCalls = new JsonArray();

        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextContent text:
                    textParts.Add(text.Text);
                    break;

                case ThinkingContent thinking when compat.RequiresThinkingAsText:
                    textParts.Add($"<thinking>\n{thinking.Thinking}\n</thinking>");
                    break;

                case ToolCallContent tc:
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = JsonSerializer.Serialize(tc.Arguments)
                        }
                    });
                    break;
            }
        }

        msg["content"] = textParts.Count > 0 ? (JsonNode?)JsonValue.Create(string.Join("", textParts)) : null;

        if (toolCalls.Count > 0)
            msg["tool_calls"] = toolCalls;

        return msg;
    }

    private static JsonObject ConvertToolResultMessage(ToolResultMessage toolResult, OpenAICompletionsCompat compat)
    {
        var content = string.Join("", toolResult.Content
            .OfType<TextContent>()
            .Select(t => t.Text));

        var msg = new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = toolResult.ToolCallId,
            ["content"] = content
        };

        if (compat.RequiresToolResultName)
            msg["name"] = toolResult.ToolName;

        return msg;
    }

    #endregion

    #region Tool Conversion

    private static JsonArray ConvertTools(IReadOnlyList<Tool> tools, OpenAICompletionsCompat compat)
    {
        var result = new JsonArray();

        foreach (var tool in tools)
        {
            var fn = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
            };

            if (compat.SupportsStrictMode)
                fn["strict"] = true;

            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = fn
            });
        }

        return result;
    }

    #endregion

    #region SSE Parsing

    private async Task ParseSseStream(
        LlmStream stream,
        StreamReader reader,
        LlmModel model,
        OpenAICompletionsCompat compat,
        CancellationToken ct)
    {
        var contentBlocks = new List<ContentBlock>();
        var usage = Usage.Empty();
        string? responseId = null;

        var currentTextIndex = -1;
        var currentThinkingIndex = -1;
        var textAccumulator = new StringBuilder();
        var thinkingAccumulator = new StringBuilder();

        // SSE tool_calls index → (Id, Name, ArgsBuilder, ContentBlockIndex)
        var toolCallState = new Dictionary<int, (string Id, string Name, StringBuilder Args, int ContentIndex)>();

        var startEmitted = false;
        StopReason? stopReason = null;

        AssistantMessage BuildPartial() => new(
            Content: contentBlocks.ToList(),
            Api: Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: usage,
            StopReason: stopReason ?? StopReason.Stop,
            ErrorMessage: null,
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
                logger.LogDebug("Skipping malformed SSE chunk");
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // Error in the SSE stream
                if (root.TryGetProperty("error", out var errorProp))
                {
                    var errorMsg = errorProp.TryGetProperty("message", out var msgProp)
                        ? msgProp.GetString() ?? "Unknown API error"
                        : "Unknown API error";

                    EmitError(stream, model, errorMsg, contentBlocks);
                    return;
                }

                if (responseId is null && root.TryGetProperty("id", out var idProp))
                    responseId = idProp.GetString();

                if (root.TryGetProperty("usage", out var usageProp) &&
                    usageProp.ValueKind == JsonValueKind.Object)
                {
                    usage = ParseUsage(usageProp, usage, model);
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
                        usage = ParseUsage(choiceUsageProp, usage, model);
                    }

                    if (choice.TryGetProperty("finish_reason", out var finishProp) &&
                        finishProp.ValueKind == JsonValueKind.String)
                    {
                        stopReason = MapStopReason(finishProp.GetString());
                    }

                    if (!choice.TryGetProperty("delta", out var delta))
                        continue;

                    if (!startEmitted)
                    {
                        stream.Push(new StartEvent(BuildPartial()));
                        startEmitted = true;
                    }

                    // --- Reasoning / thinking content ---
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
                                contentBlocks.Add(new ThinkingContent(""));
                                stream.Push(new ThinkingStartEvent(currentThinkingIndex, BuildPartial()));
                            }

                            thinkingAccumulator.Append(thinking);
                            contentBlocks[currentThinkingIndex] = new ThinkingContent(thinkingAccumulator.ToString());
                            stream.Push(new ThinkingDeltaEvent(currentThinkingIndex, thinking, BuildPartial()));
                        }
                    }

                    // --- Text content ---
                    if (delta.TryGetProperty("content", out var contentProp) &&
                        contentProp.ValueKind == JsonValueKind.String)
                    {
                        var text = contentProp.GetString() ?? "";
                        if (text.Length > 0)
                        {
                            // Close thinking block when text starts
                            if (currentThinkingIndex >= 0)
                            {
                                stream.Push(new ThinkingEndEvent(
                                    currentThinkingIndex,
                                    thinkingAccumulator.ToString(),
                                    BuildPartial()));
                                currentThinkingIndex = -1;
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

                    // --- Tool calls ---
                    if (delta.TryGetProperty("tool_calls", out var tcProp) &&
                        tcProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcProp.EnumerateArray())
                        {
                            var tcIndex = tc.TryGetProperty("index", out var idxProp)
                                ? idxProp.GetInt32()
                                : 0;

                            if (!toolCallState.ContainsKey(tcIndex))
                            {
                                // New tool call — close open text/thinking blocks
                                CloseOpenBlocks(
                                    stream, ref currentTextIndex, ref currentThinkingIndex,
                                    textAccumulator, thinkingAccumulator, contentBlocks, BuildPartial);

                                var tcId = tc.TryGetProperty("id", out var tcIdProp)
                                    ? tcIdProp.GetString() ?? ""
                                    : "";

                                var fnName = "";
                                if (tc.TryGetProperty("function", out var fnProp) &&
                                    fnProp.TryGetProperty("name", out var nameProp))
                                    fnName = nameProp.GetString() ?? "";

                                var contentIndex = contentBlocks.Count;
                                contentBlocks.Add(new ToolCallContent(tcId, fnName, []));
                                toolCallState[tcIndex] = (tcId, fnName, new StringBuilder(), contentIndex);

                                stream.Push(new ToolCallStartEvent(contentIndex, BuildPartial()));
                            }

                            // Accumulate function arguments
                            if (tc.TryGetProperty("function", out var fnDeltaProp) &&
                                fnDeltaProp.TryGetProperty("arguments", out var argsProp) &&
                                argsProp.ValueKind == JsonValueKind.String)
                            {
                                var argsChunk = argsProp.GetString() ?? "";
                                if (argsChunk.Length > 0)
                                {
                                    var state = toolCallState[tcIndex];
                                    state.Args.Append(argsChunk);
                                    toolCallState[tcIndex] = state;

                                    var parsedArgs = StreamingJsonParser.Parse(state.Args.ToString());
                                    contentBlocks[state.ContentIndex] =
                                        new ToolCallContent(state.Id, state.Name, parsedArgs);

                                    stream.Push(new ToolCallDeltaEvent(
                                        state.ContentIndex, argsChunk, BuildPartial()));
                                }
                            }
                        }
                    }
                }
            }
        }

        // Close remaining open blocks
        if (currentThinkingIndex >= 0)
            stream.Push(new ThinkingEndEvent(currentThinkingIndex, thinkingAccumulator.ToString(), BuildPartial()));

        if (currentTextIndex >= 0)
            stream.Push(new TextEndEvent(currentTextIndex, textAccumulator.ToString(), BuildPartial()));

        foreach (var (_, state) in toolCallState)
        {
            var parsedArgs = StreamingJsonParser.Parse(state.Args.ToString());
            var toolCall = new ToolCallContent(state.Id, state.Name, parsedArgs);
            contentBlocks[state.ContentIndex] = toolCall;
            stream.Push(new ToolCallEndEvent(state.ContentIndex, toolCall, BuildPartial()));
        }

        var finalMessage = BuildPartial() with { StopReason = stopReason ?? StopReason.Stop };
        stream.Push(new DoneEvent(stopReason ?? StopReason.Stop, finalMessage));
        stream.End(finalMessage);
    }

    private static void CloseOpenBlocks(
        LlmStream stream,
        ref int currentTextIndex,
        ref int currentThinkingIndex,
        StringBuilder textAccumulator,
        StringBuilder thinkingAccumulator,
        List<ContentBlock> contentBlocks,
        Func<AssistantMessage> buildPartial)
    {
        if (currentThinkingIndex >= 0)
        {
            stream.Push(new ThinkingEndEvent(currentThinkingIndex, thinkingAccumulator.ToString(), buildPartial()));
            currentThinkingIndex = -1;
        }

        if (currentTextIndex >= 0)
        {
            stream.Push(new TextEndEvent(currentTextIndex, textAccumulator.ToString(), buildPartial()));
            currentTextIndex = -1;
        }
    }

    #endregion

    #region Usage & Mapping

    private static Usage ParseUsage(JsonElement usageElement, Usage usage, LlmModel model)
    {
        var promptTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        var completionTokens = usage.Output;
        var reportedCachedTokens = usage.CacheRead;
        var cacheWriteTokens = usage.CacheWrite;
        var reasoningTokens = 0;

        if (usageElement.TryGetProperty("prompt_tokens", out var pt))
            promptTokens = pt.GetInt32();

        if (usageElement.TryGetProperty("completion_tokens", out var ct))
            completionTokens = ct.GetInt32();

        if (usageElement.TryGetProperty("prompt_tokens_details", out var ptDetails) &&
            ptDetails.TryGetProperty("cached_tokens", out var cached))
        {
            reportedCachedTokens = cached.GetInt32();
        }

        if (usageElement.TryGetProperty("prompt_tokens_details", out ptDetails) &&
            ptDetails.TryGetProperty("cache_write_tokens", out var cacheWrite))
        {
            cacheWriteTokens = cacheWrite.GetInt32();
        }

        if (usageElement.TryGetProperty("completion_tokens_details", out var completionDetails) &&
            completionDetails.TryGetProperty("reasoning_tokens", out var reasoning))
        {
            reasoningTokens = reasoning.GetInt32();
        }

        var cacheReadTokens = cacheWriteTokens > 0
            ? Math.Max(0, reportedCachedTokens - cacheWriteTokens)
            : reportedCachedTokens;

        var inputTokens = Math.Max(0, promptTokens - cacheReadTokens - cacheWriteTokens);
        var outputTokens = completionTokens + reasoningTokens;

        var updated = usage with
        {
            Input = inputTokens,
            Output = outputTokens,
            CacheRead = cacheReadTokens,
            CacheWrite = cacheWriteTokens,
            TotalTokens = inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens
        };
        updated = updated with { Cost = ModelRegistry.CalculateCost(model, updated) };
        return updated;
    }

    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "stop" => StopReason.Stop,
        "end" => StopReason.Stop,
        "length" => StopReason.Length,
        "function_call" => StopReason.ToolUse,
        "tool_calls" => StopReason.ToolUse,
        "content_filter" => StopReason.Sensitive,
        "network_error" => StopReason.Error,
        _ => StopReason.Error
    };

    private static string MapThinkingLevel(ThinkingLevel level, OpenAICompletionsCompat? compat)
    {
        if (compat?.ReasoningEffortMap is not null &&
            compat.ReasoningEffortMap.TryGetValue(level, out var mapped))
            return mapped;

        return level switch
        {
            ThinkingLevel.Minimal => "low",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "high",
            _ => "medium"
        };
    }

    #endregion

    #region Error Helpers

    private void EmitError(LlmStream stream, LlmModel model, string errorMessage,
        List<ContentBlock>? partialContent = null)
    {
        var message = new AssistantMessage(
            Content: partialContent ?? [],
            Api: Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Error,
            ErrorMessage: errorMessage,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new ErrorEvent(StopReason.Error, message));
        stream.End(message);
    }

    private void EmitAborted(LlmStream stream, LlmModel model)
    {
        var message = new AssistantMessage(
            Content: [],
            Api: Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Aborted,
            ErrorMessage: "Request was cancelled",
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new DoneEvent(StopReason.Aborted, message));
        stream.End(message);
    }

    #endregion
}
