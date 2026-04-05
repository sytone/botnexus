using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using BotNexus.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.OpenAI;

/// <summary>
/// OpenAI Responses API provider.
/// Port of pi-mono's openai-responses provider + shared stream processor.
/// </summary>
public sealed class OpenAIResponsesProvider(
    HttpClient httpClient,
    ILogger<OpenAIResponsesProvider> logger) : IApiProvider
{
    public string Api => "openai-responses";

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
                logger.LogError(ex, "OpenAI responses stream failed for model {Model}", model.Id);
                EmitError(stream, model, ex.Message);
            }
        }, ct);

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var reasoning = ModelRegistry.SupportsExtraHigh(model) ? options?.Reasoning : SimpleOptionsHelper.ClampReasoning(options?.Reasoning);
        var responsesOptions = new OpenAIResponsesOptions
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
            Metadata = options?.Metadata
        };

        if (reasoning is not null && model.Reasoning)
            responsesOptions.ReasoningEffort = MapThinkingLevel(reasoning.Value);

        return Stream(model, context, responsesOptions);
    }

    private async Task StreamCoreAsync(
        LlmStream stream,
        LlmModel model,
        Context context,
        StreamOptions? options,
        CancellationToken ct)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }

        var messages = MessageTransformer.TransformMessages(context.Messages, model);
        var payload = BuildRequestPayload(model, context.SystemPrompt, messages, context.Tools, options);

        if (options?.OnPayload is not null)
        {
            var modified = await options.OnPayload(payload, model);
            if (modified is JsonObject obj)
                payload = obj;
        }

        var url = $"{model.BaseUrl.TrimEnd('/')}/responses";
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

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        await ParseSseStream(stream, reader, model, options, ct);
    }

    private static JsonObject BuildRequestPayload(
        LlmModel model,
        string? systemPrompt,
        IReadOnlyList<Message> messages,
        IReadOnlyList<Tool>? tools,
        StreamOptions? options)
    {
        var input = ConvertMessages(messages, model);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            input.Insert(0, new JsonObject
            {
                ["type"] = "message",
                ["role"] = model.Reasoning ? "developer" : "system",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = UnicodeSanitizer.SanitizeSurrogates(systemPrompt)
                    }
                }
            });
        }

        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["store"] = false,
            ["input"] = input
        };

        if (options?.MaxTokens is not null)
            payload["max_output_tokens"] = options.MaxTokens.Value;

        if (options?.Temperature is not null)
            payload["temperature"] = options.Temperature.Value;

        if (options is OpenAIResponsesOptions { ServiceTier: not null } responsesOptions)
            payload["service_tier"] = responsesOptions.ServiceTier;

        if (options?.CacheRetention != CacheRetention.None && !string.IsNullOrWhiteSpace(options?.SessionId))
            payload["prompt_cache_key"] = options.SessionId;
        var promptCacheRetention = GetPromptCacheRetention(model.BaseUrl, options?.CacheRetention ?? CacheRetention.Short);
        if (!string.IsNullOrWhiteSpace(promptCacheRetention))
            payload["prompt_cache_retention"] = promptCacheRetention;

        if (tools is { Count: > 0 })
            payload["tools"] = ConvertTools(tools);

        var previousResponseId = options is OpenAIResponsesOptions { PreviousResponseId: not null } rspOptions
            ? rspOptions.PreviousResponseId
            : messages.OfType<AssistantMessage>().Reverse().FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.ResponseId))?.ResponseId;
        if (!string.IsNullOrWhiteSpace(previousResponseId))
            payload["previous_response_id"] = previousResponseId;

        if (model.Reasoning)
        {
            var effort = options is OpenAIResponsesOptions { ReasoningEffort: not null } ro ? ro.ReasoningEffort : null;
            var summary = options is OpenAIResponsesOptions { ReasoningSummary: not null } rs ? rs.ReasoningSummary : null;
            if (effort is not null || summary is not null)
            {
                payload["reasoning"] = new JsonObject
                {
                    ["effort"] = effort ?? "medium",
                    ["summary"] = summary ?? "auto"
                };
                payload["include"] = new JsonArray { "reasoning.encrypted_content" };
            }
            else if (!string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
            {
                payload["reasoning"] = new JsonObject
                {
                    ["effort"] = "none"
                };
            }
        }

        return payload;
    }

    private static JsonArray ConvertMessages(IReadOnlyList<Message> messages, LlmModel model)
    {
        var result = new JsonArray();

        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    result.Add(ConvertUserMessage(user, model));
                    break;

                case AssistantMessage assistant:
                    foreach (var item in ConvertAssistantMessage(assistant, model))
                        result.Add(item);
                    break;

                case ToolResultMessage toolResult:
                    result.Add(ConvertToolResultMessage(toolResult, model));
                    break;
            }
        }

        return result;
    }

    private static JsonObject ConvertUserMessage(UserMessage user, LlmModel model)
    {
        if (user.Content.IsText)
        {
            return new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = UnicodeSanitizer.SanitizeSurrogates(user.Content.Text ?? "")
                    }
                }
            };
        }

        var contentArray = new JsonArray();
        foreach (var block in user.Content.Blocks ?? [])
        {
            switch (block)
            {
                case TextContent text:
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = UnicodeSanitizer.SanitizeSurrogates(text.Text)
                    });
                    break;

                case ImageContent image when model.Input.Contains("image"):
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = $"data:{image.MimeType};base64,{image.Data}"
                    });
                    break;
            }
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = contentArray
        };
    }

    private static IReadOnlyList<JsonObject> ConvertAssistantMessage(AssistantMessage assistant, LlmModel model)
    {
        var items = new List<JsonObject>();
        var isDifferentModel = !string.Equals(assistant.ModelId, model.Id, StringComparison.Ordinal) &&
                               string.Equals(assistant.Provider, model.Provider, StringComparison.Ordinal) &&
                               string.Equals(assistant.Api, model.Api, StringComparison.Ordinal);
        var msgIndex = 0;

        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextContent textBlock:
                    var parsedSignature = ParseTextSignature(textBlock.TextSignature);
                    var msgId = parsedSignature?.Id;
                    if (string.IsNullOrWhiteSpace(msgId))
                    {
                        msgId = $"msg_{msgIndex}";
                    }
                    else if (msgId.Length > 64)
                    {
                        msgId = $"msg_{ShortHash(msgId)}";
                    }

                    var textItem = new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "output_text",
                                ["text"] = UnicodeSanitizer.SanitizeSurrogates(textBlock.Text),
                                ["annotations"] = new JsonArray()
                            }
                        },
                        ["status"] = "completed",
                        ["id"] = msgId
                    };
                    if (parsedSignature?.Phase is not null)
                        textItem["phase"] = parsedSignature.Phase;
                    items.Add(textItem);
                    msgIndex++;
                    break;

                case ThinkingContent thinking:
                    if (!string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                    {
                        try
                        {
                            var parsed = JsonNode.Parse(thinking.ThinkingSignature);
                            if (parsed is JsonObject reasoningItem)
                                items.Add(reasoningItem);
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    break;

                case ToolCallContent toolCall:
                    var (callId, itemId) = SplitToolCallId(toolCall.Id);
                    if (isDifferentModel && itemId?.StartsWith("fc_", StringComparison.Ordinal) == true)
                        itemId = null;
                    items.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = callId,
                        ["id"] = itemId,
                        ["name"] = toolCall.Name,
                        ["arguments"] = JsonSerializer.Serialize(toolCall.Arguments)
                    });
                    break;
            }
        }

        return items;
    }

    private static JsonObject ConvertToolResultMessage(ToolResultMessage toolResult, LlmModel model)
    {
        var (callId, _) = SplitToolCallId(toolResult.ToolCallId);
        var textResult = string.Join("\n", toolResult.Content.OfType<TextContent>().Select(t => t.Text));
        var hasImages = toolResult.Content.Any(c => c is ImageContent) && model.Input.Contains("image");
        JsonNode output;

        if (hasImages)
        {
            var outputParts = new JsonArray();
            if (!string.IsNullOrWhiteSpace(textResult))
            {
                outputParts.Add(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = UnicodeSanitizer.SanitizeSurrogates(textResult)
                });
            }

            foreach (var image in toolResult.Content.OfType<ImageContent>())
            {
                outputParts.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["detail"] = "auto",
                    ["image_url"] = $"data:{image.MimeType};base64,{image.Data}"
                });
            }

            output = outputParts;
        }
        else
        {
            output = JsonValue.Create(UnicodeSanitizer.SanitizeSurrogates(
                string.IsNullOrWhiteSpace(textResult) ? "(see attached image)" : textResult))!;
        }

        return new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output
        };
    }

    private static JsonArray ConvertTools(IReadOnlyList<Tool> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText()),
                ["strict"] = false
            });
        }

        return result;
    }

    private async Task ParseSseStream(
        LlmStream stream,
        StreamReader reader,
        LlmModel model,
        StreamOptions? options,
        CancellationToken ct)
    {
        var contentBlocks = new List<ContentBlock>();
        var usage = Usage.Empty();
        string? responseId = null;
        var started = false;
        var stopReason = StopReason.Stop;

        var textStates = new Dictionary<string, (int ContentIndex, StringBuilder Text)>(StringComparer.Ordinal);
        var thinkingStates = new Dictionary<string, (int ContentIndex, StringBuilder Text)>(StringComparer.Ordinal);
        var toolStates = new Dictionary<string, ToolState>(StringComparer.Ordinal);

        AssistantMessage BuildPartial() => new(
            Content: contentBlocks.ToList(),
            Api: Api,
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
                EmitError(stream, model, evt.Data);
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
                            contentBlocks.Add(new ToolCallContent(ComposeToolCallId(callId, itemId), name, parsed));
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
                        ComposeToolCallId(state.CallId, state.ItemId),
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
                        ComposeToolCallId(state.CallId, state.ItemId),
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
                                ComposeToolCallId(callId, state.ItemId),
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
                    stopReason = MapStopReason(GetString(responseEl, "status"));

                    if (responseEl.TryGetProperty("usage", out var usageEl) &&
                        usageEl.ValueKind == JsonValueKind.Object)
                    {
                        usage = ParseUsage(usageEl, model);
                        var configuredTier = options is OpenAIResponsesOptions ro ? ro.ServiceTier : null;
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
                    EmitError(stream, model, message, contentBlocks);
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

    private static Usage ParseUsage(JsonElement usageElement, LlmModel model)
    {
        var inputTokens = usageElement.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : 0;
        var outputTokens = usageElement.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : 0;
        var total = usageElement.TryGetProperty("total_tokens", out var totalEl) ? totalEl.GetInt32() : inputTokens + outputTokens;
        var cacheRead = 0;
        var cacheWrite = 0;

        if (usageElement.TryGetProperty("input_tokens_details", out var details))
        {
            if (details.TryGetProperty("cached_tokens", out var cached))
                cacheRead = cached.GetInt32();
            if (details.TryGetProperty("cache_write_tokens", out var write))
                cacheWrite = write.GetInt32();
        }

        var usage = new Usage
        {
            Input = Math.Max(0, inputTokens - cacheRead - cacheWrite),
            Output = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            TotalTokens = total
        };

        return usage with { Cost = ModelRegistry.CalculateCost(model, usage) };
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

    private static StopReason MapStopReason(string? status) => status switch
    {
        "completed" => StopReason.Stop,
        "incomplete" => StopReason.Length,
        "failed" => StopReason.Error,
        "cancelled" => StopReason.Error,
        "in_progress" => StopReason.Stop,
        "queued" => StopReason.Stop,
        _ => StopReason.Stop
    };

    private static string MapThinkingLevel(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        _ => "medium"
    };

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

    private sealed record ParsedTextSignature(string Id, string? Phase);

    private static ParsedTextSignature? ParseTextSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        if (signature.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(signature);
                var root = doc.RootElement;
                if (root.TryGetProperty("v", out var version) &&
                    version.ValueKind == JsonValueKind.Number &&
                    version.GetInt32() == 1 &&
                    root.TryGetProperty("id", out var idProp) &&
                    idProp.ValueKind == JsonValueKind.String)
                {
                    var id = idProp.GetString()!;
                    var phase = root.TryGetProperty("phase", out var phaseProp) &&
                                phaseProp.ValueKind == JsonValueKind.String
                        ? phaseProp.GetString()
                        : null;
                    if (phase is not ("commentary" or "final_answer"))
                        phase = null;
                    return new ParsedTextSignature(id, phase);
                }
            }
            catch (JsonException)
            {
            }
        }

        return new ParsedTextSignature(signature, null);
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
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

    private static string? GetPromptCacheRetention(string baseUrl, CacheRetention retention)
    {
        if (retention != CacheRetention.Long)
            return null;

        return baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase) ? "24h" : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private static (string CallId, string? ItemId) SplitToolCallId(string id)
    {
        if (!id.Contains('|')) return (id, null);
        var parts = id.Split('|', 2);
        return (parts[0], parts[1]);
    }

    private static string ComposeToolCallId(string callId, string? itemId)
        => string.IsNullOrWhiteSpace(itemId) ? callId : $"{callId}|{itemId}";

    private void EmitError(
        LlmStream stream,
        LlmModel model,
        string errorMessage,
        IReadOnlyList<ContentBlock>? partialContent = null)
    {
        var message = new AssistantMessage(
            Content: partialContent?.ToList() ?? [],
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

    private sealed record SseEvent(string Event, string Data);

    private sealed class ToolState(string callId, string? itemId, string name, int contentIndex)
    {
        public string CallId { get; } = callId;
        public string? ItemId { get; } = itemId;
        public string Name { get; } = name;
        public int ContentIndex { get; } = contentIndex;
        public StringBuilder Arguments { get; } = new();
    }
}
