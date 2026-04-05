using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Compatibility;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using BotNexus.Providers.Core.Utilities;

namespace BotNexus.Providers.OpenAICompat;

/// <summary>
/// Provider for OpenAI-compatible APIs (Ollama, vLLM, LM Studio, SGLang, etc.).
/// Uses raw HttpClient — no external SDK dependency.
/// </summary>
public sealed class OpenAICompatProvider(HttpClient httpClient) : IApiProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public string Api => "openai-compat";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamCoreAsync(model, context, options, stream);
            }
            catch (Exception ex)
            {
                var errorMessage = CreateErrorMessage(model, ex.Message);
                stream.Push(new ErrorEvent(StopReason.Error, errorMessage));
                stream.End(errorMessage);
            }
        });

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var baseOptions = SimpleOptionsHelper.BuildBaseOptions(model, options, apiKey);

        // Apply reasoning effort if supported
        if (options?.Reasoning is not null)
        {
            var compat = CompatDetector.Detect(model);
            if (compat.SupportsReasoningEffort && compat.ReasoningEffortMap is not null)
            {
                var compatOptions = new OpenAICompatOptions
                {
                    Temperature = baseOptions.Temperature,
                    MaxTokens = baseOptions.MaxTokens,
                    CancellationToken = baseOptions.CancellationToken,
                    ApiKey = baseOptions.ApiKey,
                    Transport = baseOptions.Transport,
                    CacheRetention = baseOptions.CacheRetention,
                    SessionId = baseOptions.SessionId,
                    OnPayload = baseOptions.OnPayload,
                    Headers = baseOptions.Headers,
                    MaxRetryDelayMs = baseOptions.MaxRetryDelayMs,
                    Metadata = baseOptions.Metadata,
                    ReasoningEffort = compat.ReasoningEffortMap.TryGetValue(options.Reasoning.Value, out var effort)
                        ? effort
                        : "medium",
                };
                return Stream(model, context, compatOptions);
            }
        }

        return Stream(model, context, baseOptions);
    }

    private async Task StreamCoreAsync(
        LlmModel model, Context context, StreamOptions? options, LlmStream stream)
    {
        var compat = CompatDetector.Detect(model);
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var ct = options?.CancellationToken ?? CancellationToken.None;

        var requestBody = BuildRequestBody(model, context, options, compat);

        // Fire onPayload callback if provided
        if (options?.OnPayload is not null)
        {
            var modified = await options.OnPayload(requestBody, model);
            if (modified is JsonElement modifiedElement)
                requestBody = modifiedElement;
        }

        var requestJson = JsonSerializer.Serialize(requestBody);
        var url = $"{model.BaseUrl.TrimEnd('/')}/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // Apply model-level headers
        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        // Apply request-level headers
        if (options?.Headers is not null)
        {
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var errorMessage = CreateErrorMessage(model, $"HTTP {(int)response.StatusCode}: {errorBody}");
            stream.Push(new ErrorEvent(StopReason.Error, errorMessage));
            stream.End(errorMessage);
            return;
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        var output = CreatePartialMessage(model);
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

            // SSE format: "data: {json}" or "data: [DONE]"
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line[6..];

            if (data == "[DONE]")
                break;

            JsonElement chunk;
            try
            {
                chunk = JsonDocument.Parse(data).RootElement;
            }
            catch (JsonException)
            {
                continue; // Skip malformed chunks
            }

            responseId ??= chunk.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            // Parse usage from the chunk (some servers send it in the final chunk)
            if (chunk.TryGetProperty("usage", out var usageProp))
                output = output with { Usage = ParseUsage(usageProp, output.Usage, model) };

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

            // Text content delta
            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var text = contentProp.GetString() ?? "";
                if (text.Length > 0)
                {
                    var sanitized = UnicodeSanitizer.SanitizeSurrogates(text);

                    if (contentBuilder.Length == 0)
                        stream.Push(new TextStartEvent(contentIndex, output));

                    contentBuilder.Append(sanitized);
                    UpdateOutputContent(output, contentBuilder, toolCallBuilders);
                    stream.Push(new TextDeltaEvent(contentIndex, sanitized, output));
                }
            }

            // Tool call deltas
            if (delta.TryGetProperty("tool_calls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCallsProp.EnumerateArray())
                {
                    var tcIndex = tc.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : 0;

                    if (!toolCallBuilders.TryGetValue(tcIndex, out var builder))
                    {
                        // Finish text content block if one is in progress
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
                            UpdateOutputContent(output, contentBuilder, toolCallBuilders);
                            stream.Push(new ToolCallDeltaEvent(contentIndex + tcIndex, argChunk, output));
                        }
                    }
                }
            }
        }

        // Finalize any open text content
        if (contentBuilder.Length > 0 && toolCallBuilders.Count == 0)
            stream.Push(new TextEndEvent(contentIndex, contentBuilder.ToString(), output));

        // Finalize tool calls
        foreach (var (tcIndex, builder) in toolCallBuilders)
        {
            var args = StreamingJsonParser.Parse(builder.ArgumentsJson.ToString());
            var toolCall = new ToolCallContent(builder.Id, builder.Name ?? "", args);
            stream.Push(new ToolCallEndEvent(contentIndex + tcIndex, toolCall, output));
        }

        // Determine stop reason
        var stopReason = finishReason switch
        {
            "stop" => StopReason.Stop,
            "length" => StopReason.Length,
            "tool_calls" => StopReason.ToolUse,
            _ => toolCallBuilders.Count > 0 ? StopReason.ToolUse : StopReason.Stop,
        };

        // Build final message
        var finalContent = BuildFinalContent(contentBuilder, toolCallBuilders);
        var finalMessage = new AssistantMessage(
            Content: finalContent,
            Api: "openai-compat",
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: output.Usage,
            StopReason: stopReason,
            ErrorMessage: null,
            ResponseId: responseId,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        stream.Push(new DoneEvent(stopReason, finalMessage));
        stream.End(finalMessage);
    }

    private static object BuildRequestBody(
        LlmModel model, Context context, StreamOptions? options, OpenAICompletionsCompat compat)
    {
        var messages = BuildMessages(context, compat, model);
        var body = new Dictionary<string, object?>
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["stream"] = true,
        };

        // Max tokens
        var maxTokens = options?.MaxTokens ?? model.MaxTokens;
        body[compat.MaxTokensField] = maxTokens;

        // Temperature
        if (options?.Temperature is not null)
            body["temperature"] = options.Temperature;

        // Store
        if (compat.SupportsStore)
            body["store"] = true;

        // Stream options for usage in streaming
        if (compat.SupportsUsageInStreaming)
            body["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };

        // Reasoning effort
        if (options is OpenAICompatOptions compatOpts && compatOpts.ReasoningEffort is not null && compat.SupportsReasoningEffort)
            body["reasoning_effort"] = compatOpts.ReasoningEffort;

        // Tool choice
        if (options is OpenAICompatOptions compatOpts2 && compatOpts2.ToolChoice is not null)
            body["tool_choice"] = compatOpts2.ToolChoice;

        // Tools
        if (context.Tools is { Count: > 0 })
        {
            var tools = new List<object>();
            foreach (var tool in context.Tools)
            {
                var fn = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.Parameters,
                };

                if (compat.SupportsStrictMode)
                    fn["strict"] = true;

                tools.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = fn,
                });
            }
            body["tools"] = tools;
        }

        return body;
    }

    private static List<Dictionary<string, object?>> BuildMessages(
        Context context, OpenAICompletionsCompat compat, LlmModel model)
    {
        var messages = new List<Dictionary<string, object?>>();
        var supportsImages = model.Input.Contains("image");

        // System prompt
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            var role = compat.SupportsDeveloperRole ? "developer" : "system";
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = role,
                ["content"] = context.SystemPrompt,
            });
        }

        for (var i = 0; i < context.Messages.Count; i++)
        {
            var msg = context.Messages[i];

            switch (msg)
            {
                case UserMessage user:
                    messages.Add(BuildUserMessage(user, supportsImages));
                    break;

                case AssistantMessage assistant:
                    messages.Add(BuildAssistantMessage(assistant, compat));
                    break;

                case ToolResultMessage toolResult:
                    messages.Add(BuildToolResultMessage(toolResult, compat));

                    // Insert synthetic assistant message after tool result if required
                    if (compat.RequiresAssistantAfterToolResult)
                    {
                        var nextIsAssistant = i + 1 < context.Messages.Count
                            && context.Messages[i + 1] is AssistantMessage;
                        if (!nextIsAssistant)
                        {
                            messages.Add(new Dictionary<string, object?>
                            {
                                ["role"] = "assistant",
                                ["content"] = "",
                            });
                        }
                    }
                    break;
            }
        }

        return messages;
    }

    private static Dictionary<string, object?> BuildUserMessage(UserMessage user, bool supportsImages)
    {
        if (user.Content.IsText)
        {
            return new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = user.Content.Text,
            };
        }

        // Structured content blocks
        var parts = new List<object>();
        if (user.Content.Blocks is not null)
        {
            foreach (var block in user.Content.Blocks)
            {
                switch (block)
                {
                    case TextContent text:
                        parts.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
                        break;

                    case ImageContent image when supportsImages:
                        parts.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object?>
                            {
                                ["url"] = $"data:{image.MimeType};base64,{image.Data}",
                            },
                        });
                        break;

                    case ImageContent:
                        // Server doesn't support images — skip
                        break;
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = parts,
        };
    }

    private static Dictionary<string, object?> BuildAssistantMessage(
        AssistantMessage assistant, OpenAICompletionsCompat compat)
    {
        var msg = new Dictionary<string, object?> { ["role"] = "assistant" };

        var textParts = new List<string>();
        var toolCalls = new List<object>();

        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextContent text:
                    textParts.Add(text.Text);
                    break;

                case ThinkingContent thinking when compat.RequiresThinkingAsText:
                    textParts.Add($"<thinking>{thinking.Thinking}</thinking>");
                    break;

                case ToolCallContent toolCall:
                    toolCalls.Add(new Dictionary<string, object?>
                    {
                        ["id"] = toolCall.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = JsonSerializer.Serialize(toolCall.Arguments),
                        },
                    });
                    break;
            }
        }

        if (toolCalls.Count > 0)
        {
            msg["tool_calls"] = toolCalls;
            if (textParts.Count > 0)
                msg["content"] = string.Join("\n", textParts);
        }
        else
        {
            msg["content"] = string.Join("\n", textParts);
        }

        return msg;
    }

    private static Dictionary<string, object?> BuildToolResultMessage(
        ToolResultMessage toolResult, OpenAICompletionsCompat compat)
    {
        var contentText = string.Join("\n", toolResult.Content
            .OfType<TextContent>()
            .Select(t => t.Text));

        var msg = new Dictionary<string, object?>
        {
            ["role"] = "tool",
            ["tool_call_id"] = toolResult.ToolCallId,
            ["content"] = contentText,
        };

        if (compat.RequiresToolResultName)
            msg["name"] = toolResult.ToolName;

        return msg;
    }

    private static Usage ParseUsage(JsonElement usageProp, Usage usage, LlmModel model)
    {
        var updated = usage;
        if (usageProp.TryGetProperty("prompt_tokens", out var inputTokens))
            updated = updated with { Input = inputTokens.GetInt32() };
        if (usageProp.TryGetProperty("completion_tokens", out var outputTokens))
            updated = updated with { Output = outputTokens.GetInt32() };
        if (usageProp.TryGetProperty("total_tokens", out var totalTokens))
            updated = updated with { TotalTokens = totalTokens.GetInt32() };

        return updated with { Cost = ModelRegistry.CalculateCost(model, updated) };
    }

    private static void UpdateOutputContent(
        AssistantMessage output, StringBuilder contentBuilder, Dictionary<int, ToolCallBuilder> toolCallBuilders)
    {
        var blocks = new List<ContentBlock>();
        if (contentBuilder.Length > 0)
            blocks.Add(new TextContent(contentBuilder.ToString()));
        foreach (var (_, builder) in toolCallBuilders)
        {
            var args = StreamingJsonParser.Parse(builder.ArgumentsJson.ToString());
            blocks.Add(new ToolCallContent(builder.Id, builder.Name ?? "", args));
        }
        // Note: AssistantMessage is a record and Content is init-only.
        // We rebuild output at the end; intermediate events carry partial snapshots via the stream events themselves.
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

    private static AssistantMessage CreatePartialMessage(LlmModel model)
    {
        return new AssistantMessage(
            Content: [],
            Api: "openai-compat",
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }

    private static AssistantMessage CreateErrorMessage(LlmModel model, string error)
    {
        return new AssistantMessage(
            Content: [new TextContent(error)],
            Api: "openai-compat",
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Error,
            ErrorMessage: error,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }

    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public StringBuilder ArgumentsJson { get; } = new();
    }
}
