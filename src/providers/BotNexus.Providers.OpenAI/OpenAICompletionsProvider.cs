using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
public sealed partial class OpenAICompletionsProvider(
    HttpClient httpClient,
    ILogger<OpenAICompletionsProvider> logger) : IApiProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private static readonly IReadOnlyDictionary<string, Action<CompatFlags>> CompatProfiles = new Dictionary<string, Action<CompatFlags>>
    {
        ["cerebras"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
        },
        ["xai"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
            flags.SupportsReasoningEffort = false;
        },
        ["zai"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
            flags.SupportsReasoningEffort = false;
            flags.ThinkingFormat = "zai";
        },
        ["deepseek"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
        },
        ["chutes"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
            flags.MaxTokensField = "max_tokens";
        },
        ["groq"] = flags =>
        {
            flags.SupportsStore = false;
            flags.SupportsStoreParam = false;
            flags.SupportsDeveloperRole = false;
            flags.SupportsMetadata = false;
        },
        ["openrouter"] = flags => flags.ThinkingFormat = "openrouter"
    };

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
            completionsOptions.ReasoningEffort = MapThinkingLevel(options.Reasoning.Value, GetCompat(model));

        return Stream(model, context, completionsOptions);
    }

    private async Task StreamCoreAsync(
        LlmStream stream,
        LlmModel model,
        Context context,
        StreamOptions? options,
        CancellationToken ct)
    {
        var compat = GetCompat(model);
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
            var providerError = ExtractProviderErrorMessage(errorBody, model);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {providerError}");
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

        if (compat.SupportsTemperature != false && options?.Temperature is not null)
            payload["temperature"] = options.Temperature.Value;

        if (compat.SupportsStore == true && compat.SupportsStoreParam != false)
            payload["store"] = false;

        if (compat.SupportsMetadata != false && options?.Metadata is { Count: > 0 })
            payload["metadata"] = JsonSerializer.SerializeToNode(options.Metadata);

        if (compat.SupportsUsageInStreaming != false)
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };

        // Reasoning / thinking support
        if (options is OpenAICompletionsOptions { ReasoningEffort: not null } compOptions && model.Reasoning)
        {
            if (compat.ThinkingFormat is "openai" && compat.SupportsReasoningEffort != false)
            {
                payload["reasoning_effort"] = compOptions.ReasoningEffort;
            }
            else if (compat.ThinkingFormat is "qwen")
            {
                payload["enable_thinking"] = true;
            }
            else if (compat.ThinkingFormat is "qwen-chat-template")
            {
                payload["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true };
            }
            else if (compat.ThinkingFormat is "openrouter")
            {
                payload["reasoning"] = new JsonObject
                {
                    ["effort"] = compOptions.ReasoningEffort
                };
            }
            else
            {
                payload["enable_thinking"] = true;
                payload["thinking_format"] = compat.ThinkingFormat;
            }
        }
        else if (compat.ThinkingFormat is "openrouter" && model.Reasoning)
        {
            payload["reasoning"] = new JsonObject
            {
                ["effort"] = "none"
            };
        }

        if (options is OpenAICompletionsOptions { ToolChoice: not null } tcOptions)
            payload["tool_choice"] = tcOptions.ToolChoice;

        payload["messages"] = ConvertMessages(systemPrompt, model, messages, compat);

        if (tools is { Count: > 0 })
        {
            payload["tools"] = ConvertTools(tools, compat);
            if (compat.ZaiToolStream == true)
                payload["tool_stream"] = true;
        }
        else if (HasToolHistory(messages))
        {
            payload["tools"] = new JsonArray();
        }

        if (model.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase) &&
            compat.OpenRouterRouting is { Count: > 0 })
        {
            payload["provider"] = JsonSerializer.SerializeToNode(compat.OpenRouterRouting);
        }

        if (model.BaseUrl.Contains("ai-gateway.vercel.sh", StringComparison.OrdinalIgnoreCase) &&
            compat.VercelGatewayRouting is { } routing &&
            (routing.Only is { Count: > 0 } || routing.Order is { Count: > 0 }))
        {
            var gateway = new JsonObject();
            if (routing.Only is { Count: > 0 })
                gateway["only"] = JsonSerializer.SerializeToNode(routing.Only);
            if (routing.Order is { Count: > 0 })
                gateway["order"] = JsonSerializer.SerializeToNode(routing.Order);
            payload["providerOptions"] = new JsonObject
            {
                ["gateway"] = gateway
            };
        }

        MaybeAddOpenRouterAnthropicCacheControl(model, payload);

        return payload;
    }

    #endregion

    #region Message Conversion

    private static bool HasToolHistory(IReadOnlyList<Message> messages)
    {
        foreach (var message in messages)
        {
            if (message is ToolResultMessage)
                return true;

            if (message is AssistantMessage assistant &&
                assistant.Content.Any(block => block is ToolCallContent))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeToolCallId(string id, LlmModel model)
    {
        if (id.Contains('|'))
        {
            var callId = id.Split('|', 2)[0];
            return NormalizeToolCallIdPart(callId, 40);
        }

        if (string.Equals(model.Provider, "openai", StringComparison.OrdinalIgnoreCase) && id.Length > 40)
            return id[..40];

        return id;
    }

    private static string NormalizeToolCallIdPart(string id, int maxLength)
    {
        var normalized = NonAlphanumericRegex().Replace(id, "_");
        return normalized.Length > maxLength ? normalized[..maxLength] : normalized;
    }

    private static void MaybeAddOpenRouterAnthropicCacheControl(LlmModel model, JsonObject payload)
    {
        if (!string.Equals(model.Provider, "openrouter", StringComparison.OrdinalIgnoreCase) ||
            !model.Id.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) ||
            payload["messages"] is not JsonArray messages)
        {
            return;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message)
                continue;

            var role = message["role"]?.GetValue<string>();
            if (role is not ("user" or "assistant"))
                continue;

            var content = message["content"];
            if (content is JsonValue stringContent)
            {
                message["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = stringContent.ToString(),
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                    }
                };
                return;
            }

            if (content is not JsonArray contentParts)
                continue;

            for (var j = contentParts.Count - 1; j >= 0; j--)
            {
                if (contentParts[j] is JsonObject part &&
                    string.Equals(part["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    part["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                    return;
                }
            }
        }
    }

    private static JsonArray ConvertMessages(
        string? systemPrompt,
        LlmModel model,
        IReadOnlyList<Message> messages,
        OpenAICompletionsCompat compat)
    {
        var result = new JsonArray();
        var transformedMessages = MessageTransformer.TransformMessages(messages, model, id => NormalizeToolCallId(id, model));
        string? lastRole = null;

        if (systemPrompt is not null)
        {
            var role = model.Reasoning && compat.SupportsDeveloperRole != false ? "developer" : "system";
            result.Add(new JsonObject { ["role"] = role, ["content"] = systemPrompt });
        }

        for (var i = 0; i < transformedMessages.Count; i++)
        {
            var message = transformedMessages[i];
            if (compat.RequiresAssistantAfterToolResult == true &&
                lastRole == "toolResult" &&
                message is UserMessage)
            {
                result.Add(new JsonObject { ["role"] = "assistant", ["content"] = "I have processed the tool results." });
            }

            switch (message)
            {
                case UserMessage user:
                    result.Add(ConvertUserMessage(user));
                    lastRole = "user";
                    break;

                case AssistantMessage assistant:
                    var assistantMessage = ConvertAssistantMessage(assistant, compat, model);
                    if (assistantMessage is not null)
                    {
                        result.Add(assistantMessage);
                        lastRole = "assistant";
                    }
                    break;

                case ToolResultMessage toolResult:
                {
                    var imageBlocks = new JsonArray();
                    var j = i;
                    for (; j < transformedMessages.Count && transformedMessages[j] is ToolResultMessage; j++)
                    {
                        var tr = (ToolResultMessage)transformedMessages[j];
                        result.Add(ConvertToolResultMessage(tr, compat));

                        var hasImages = tr.Content.Any(c => c is ImageContent);
                        if (hasImages && model.Input.Contains("image"))
                        {
                            foreach (var image in tr.Content.OfType<ImageContent>())
                            {
                                imageBlocks.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:{image.MimeType};base64,{image.Data}"
                                    }
                                });
                            }
                        }
                    }

                    i = j - 1;
                    if (imageBlocks.Count > 0)
                    {
                        if (compat.RequiresAssistantAfterToolResult == true)
                        {
                            result.Add(new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = "I have processed the tool results."
                            });
                        }

                        var userContent = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = "Attached image(s) from tool result:" }
                        };
                        foreach (var image in imageBlocks)
                            userContent.Add(image?.DeepClone());

                        result.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = userContent
                        });
                        lastRole = "user";
                    }
                    else
                    {
                        lastRole = "toolResult";
                    }

                    break;
                }
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

    private static JsonObject? ConvertAssistantMessage(AssistantMessage assistant, OpenAICompletionsCompat compat, LlmModel model)
    {
        var msg = new JsonObject { ["role"] = "assistant" };
        var textParts = new List<string>();
        var thinkingParts = new List<string>();
        var toolCalls = new JsonArray();
        var reasoningDetails = new JsonArray();

        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextContent text:
                    if (!string.IsNullOrWhiteSpace(text.Text))
                        textParts.Add(UnicodeSanitizer.SanitizeSurrogates(text.Text));
                    break;

                case ThinkingContent thinking:
                    if (string.IsNullOrWhiteSpace(thinking.Thinking))
                        break;

                    if (compat.RequiresThinkingAsText == true)
                    {
                        thinkingParts.Add(UnicodeSanitizer.SanitizeSurrogates(thinking.Thinking));
                    }
                    else if (!string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                    {
                        var signature = thinking.ThinkingSignature;
                        msg[signature] = string.Join("\n", assistant.Content.OfType<ThinkingContent>()
                            .Where(t => !string.IsNullOrWhiteSpace(t.Thinking))
                            .Select(t => UnicodeSanitizer.SanitizeSurrogates(t.Thinking)));
                    }
                    break;

                case ToolCallContent tc:
                    if (!string.IsNullOrWhiteSpace(tc.ThoughtSignature))
                    {
                        try
                        {
                            var parsed = JsonNode.Parse(tc.ThoughtSignature);
                            if (parsed is not null)
                                reasoningDetails.Add(parsed);
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = NormalizeToolCallId(tc.Id, model),
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

        if (thinkingParts.Count > 0)
            textParts.Insert(0, string.Join("\n\n", thinkingParts));

        var textContent = textParts.Count > 0 ? string.Join("", textParts) : null;
        if (string.IsNullOrWhiteSpace(textContent) && toolCalls.Count == 0)
            return null;

        msg["content"] = string.IsNullOrWhiteSpace(textContent) ? null : JsonValue.Create(textContent);

        if (toolCalls.Count > 0)
            msg["tool_calls"] = toolCalls;
        if (reasoningDetails.Count > 0)
            msg["reasoning_details"] = reasoningDetails;

        return msg;
    }

    private static JsonObject ConvertToolResultMessage(ToolResultMessage toolResult, OpenAICompletionsCompat compat)
    {
        var content = string.Join("\n", toolResult.Content
            .OfType<TextContent>()
            .Select(t => UnicodeSanitizer.SanitizeSurrogates(t.Text)));

        var msg = new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = toolResult.ToolCallId,
            ["content"] = string.IsNullOrWhiteSpace(content) ? "(see attached image)" : content
        };

        if (compat.RequiresToolResultName == true)
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

            if (compat.SupportsStrictMode != false)
                fn["strict"] = false;

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

        // SSE tool_calls index → (Id, Name, ArgsBuilder, ContentBlockIndex, ThoughtSignature)
        var toolCallState = new Dictionary<int, (string Id, string Name, StringBuilder Args, int ContentIndex, string? ThoughtSignature)>();

        var startEmitted = false;
        StopReason? stopReason = null;
        string? errorMessage = null;

        AssistantMessage BuildPartial() => new(
            Content: contentBlocks.ToList(),
            Api: Api,
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
                logger.LogDebug("Skipping malformed SSE chunk");
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // Error in the SSE stream
                if (root.TryGetProperty("error", out var errorProp))
                {
                    var errorMsg = ExtractProviderErrorMessage(root.GetRawText(), model);

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
                        var mapped = MapStopReason(finishProp.GetString());
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
                            string? thoughtSignature = null;
                            if (delta.TryGetProperty("reasoning_details", out var reasoningDetailsProp) &&
                                reasoningDetailsProp.ValueKind == JsonValueKind.Array &&
                                reasoningDetailsProp.GetArrayLength() > tcIndex)
                            {
                                thoughtSignature = reasoningDetailsProp[tcIndex].GetRawText();
                            }

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
                                toolCallState[tcIndex] = (tcId, fnName, new StringBuilder(), contentIndex, thoughtSignature);

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

        // Close remaining open blocks
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

    private static (StopReason StopReason, string? ErrorMessage) MapStopReason(string? reason) => reason switch
    {
        "stop" => (StopReason.Stop, null),
        "end" => (StopReason.Stop, null),
        "length" => (StopReason.Length, null),
        "function_call" => (StopReason.ToolUse, null),
        "tool_calls" => (StopReason.ToolUse, null),
        "content_filter" => (StopReason.Sensitive, null),
        "refusal" => (StopReason.Refusal, null),
        "network_error" => (StopReason.Error, "Provider finish_reason: network_error"),
        null => (StopReason.Stop, null),
        _ => (StopReason.Error, $"Provider finish_reason: {reason}")
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
            ThinkingLevel.ExtraHigh => "xhigh",
            _ => "medium"
        };
    }

    private static OpenAICompletionsCompat GetCompat(LlmModel model)
    {
        var detected = DetectCompat(model);
        var configured = model.Compat;
        if (configured is null)
            return detected;

        return detected with
        {
            SupportsStoreParam = configured.SupportsStoreParam ?? detected.SupportsStoreParam,
            SupportsStore = configured.SupportsStore ?? detected.SupportsStore,
            SupportsDeveloperRole = configured.SupportsDeveloperRole ?? detected.SupportsDeveloperRole,
            SupportsTemperature = configured.SupportsTemperature ?? detected.SupportsTemperature,
            SupportsMetadata = configured.SupportsMetadata ?? detected.SupportsMetadata,
            SupportsReasoningEffort = configured.SupportsReasoningEffort ?? detected.SupportsReasoningEffort,
            ReasoningEffortMap = configured.ReasoningEffortMap ?? detected.ReasoningEffortMap,
            SupportsUsageInStreaming = configured.SupportsUsageInStreaming ?? detected.SupportsUsageInStreaming,
            MaxTokensField = configured.MaxTokensField,
            RequiresToolResultName = configured.RequiresToolResultName ?? detected.RequiresToolResultName,
            RequiresAssistantAfterToolResult = configured.RequiresAssistantAfterToolResult ?? detected.RequiresAssistantAfterToolResult,
            RequiresThinkingAsText = configured.RequiresThinkingAsText ?? detected.RequiresThinkingAsText,
            ThinkingFormat = configured.ThinkingFormat,
            OpenRouterRouting = configured.OpenRouterRouting ?? detected.OpenRouterRouting,
            VercelGatewayRouting = configured.VercelGatewayRouting ?? detected.VercelGatewayRouting,
            ZaiToolStream = configured.ZaiToolStream ?? detected.ZaiToolStream,
            SupportsStrictMode = configured.SupportsStrictMode ?? detected.SupportsStrictMode
        };
    }

    private static OpenAICompletionsCompat DetectCompat(LlmModel model)
    {
        var provider = model.Provider.ToLowerInvariant();
        var baseUrl = model.BaseUrl.ToLowerInvariant();
        var flags = new CompatFlags();

        var matches = new Dictionary<string, bool>
        {
            ["cerebras"] = provider == "cerebras" || baseUrl.Contains("cerebras.ai"),
            ["xai"] = provider == "xai" || baseUrl.Contains("api.x.ai"),
            ["zai"] = provider == "zai" || baseUrl.Contains("api.z.ai"),
            ["deepseek"] = provider == "deepseek" || baseUrl.Contains("deepseek.com"),
            ["chutes"] = baseUrl.Contains("chutes.ai"),
            ["groq"] = provider == "groq" || baseUrl.Contains("groq.com"),
            ["openrouter"] = provider == "openrouter" || baseUrl.Contains("openrouter.ai")
        };

        foreach (var (key, isMatch) in matches)
        {
            if (!isMatch) continue;
            CompatProfiles[key](flags);
        }

        if (matches["groq"] && string.Equals(model.Id, "qwen/qwen3-32b", StringComparison.Ordinal))
        {
            flags.ReasoningEffortMap = new Dictionary<ThinkingLevel, string>
            {
                [ThinkingLevel.Minimal] = "default",
                [ThinkingLevel.Low] = "default",
                [ThinkingLevel.Medium] = "default",
                [ThinkingLevel.High] = "default",
                [ThinkingLevel.ExtraHigh] = "default"
            };
        }

        if (model.Id.Contains("qwen-chat-template", StringComparison.OrdinalIgnoreCase))
            flags.ThinkingFormat = "qwen-chat-template";
        else if (model.Id.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase))
            flags.ThinkingFormat = "qwen";

        return new OpenAICompletionsCompat
        {
            SupportsStoreParam = flags.SupportsStoreParam,
            SupportsStore = flags.SupportsStore,
            SupportsDeveloperRole = flags.SupportsDeveloperRole,
            SupportsTemperature = flags.SupportsTemperature,
            SupportsMetadata = flags.SupportsMetadata,
            SupportsReasoningEffort = flags.SupportsReasoningEffort,
            ReasoningEffortMap = flags.ReasoningEffortMap,
            SupportsUsageInStreaming = true,
            MaxTokensField = flags.MaxTokensField,
            RequiresToolResultName = false,
            RequiresAssistantAfterToolResult = false,
            RequiresThinkingAsText = false,
            ThinkingFormat = flags.ThinkingFormat,
            OpenRouterRouting = [],
            VercelGatewayRouting = new(),
            ZaiToolStream = false,
            SupportsStrictMode = true
        };
    }

    private sealed class CompatFlags
    {
        public bool SupportsStoreParam { get; set; } = true;
        public bool SupportsStore { get; set; } = true;
        public bool SupportsDeveloperRole { get; set; } = true;
        public bool SupportsTemperature { get; set; } = true;
        public bool SupportsMetadata { get; set; } = true;
        public bool SupportsReasoningEffort { get; set; } = true;
        public string MaxTokensField { get; set; } = "max_completion_tokens";
        public string ThinkingFormat { get; set; } = "openai";
        public Dictionary<ThinkingLevel, string>? ReasoningEffortMap { get; set; }
    }

    private static string ExtractProviderErrorMessage(string rawError, LlmModel model)
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return "Unknown API error";

        try
        {
            using var doc = JsonDocument.Parse(rawError);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageEl)
                    ? messageEl.GetString()
                    : null;

                if (string.Equals(model.Provider, "openrouter", StringComparison.OrdinalIgnoreCase) &&
                    error.TryGetProperty("metadata", out var metadata) &&
                    metadata.ValueKind == JsonValueKind.Object)
                {
                    var code = metadata.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                    var providerName = metadata.TryGetProperty("provider_name", out var providerEl) ? providerEl.GetString() : null;
                    var suffix = string.Join(", ", new[] { code, providerName }.Where(v => !string.IsNullOrWhiteSpace(v)));
                    if (!string.IsNullOrWhiteSpace(suffix))
                        return $"{message ?? "OpenRouter error"} ({suffix})";
                }

                return message ?? rawError;
            }
        }
        catch (JsonException)
        {
        }

        return rawError;
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

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NonAlphanumericRegex();
}
