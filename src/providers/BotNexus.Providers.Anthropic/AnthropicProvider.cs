using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using BotNexus.Providers.Core.Utilities;

namespace BotNexus.Providers.Anthropic;

/// <summary>
/// Anthropic Messages API provider. Port of pi-mono's providers/anthropic.ts.
/// Handles SSE streaming, three auth modes, thinking configuration, and message conversion.
/// </summary>
public sealed partial class AnthropicProvider(HttpClient httpClient) : IApiProvider
{
    private const string ApiVersion = "2023-06-01";
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string Api => "anthropic-messages";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            var usage = Usage.Empty();
            string? responseId = null;
            var stopReason = StopReason.Stop;
            var contentBlocks = new List<ContentBlock>();

            try
            {
                await StreamCoreAsync(model, context, options, stream,
                    contentBlocks, usage,
                    updatedUsage => usage = updatedUsage,
                    id => responseId = id,
                    reason => stopReason = reason, ct);

                var final = BuildMessage(model, contentBlocks, usage, stopReason, null, responseId);
                stream.Push(new DoneEvent(stopReason, final));
                stream.End(final);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var msg = BuildMessage(model, contentBlocks, usage, StopReason.Aborted, null, responseId);
                stream.Push(new ErrorEvent(StopReason.Aborted, msg));
                stream.End(msg);
            }
            catch (Exception ex)
            {
                var msg = BuildMessage(model, contentBlocks, usage, StopReason.Error, ex.Message, responseId);
                stream.Push(new ErrorEvent(StopReason.Error, msg));
                stream.End(msg);
            }
        }, ct);

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var baseOptions = SimpleOptionsHelper.BuildBaseOptions(model, options, apiKey);

        var anthropicOpts = new AnthropicOptions
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
        };

        if (options?.Reasoning is { } reasoning)
        {
            var clamped = SimpleOptionsHelper.ClampReasoning(reasoning);

            if (IsAdaptiveThinkingModel(model.Id))
            {
                anthropicOpts.ThinkingEnabled = true;
                anthropicOpts.Effort = clamped switch
                {
                    ThinkingLevel.Minimal => "low",
                    ThinkingLevel.Low => "low",
                    ThinkingLevel.Medium => "medium",
                    ThinkingLevel.High => "high",
                    ThinkingLevel.ExtraHigh => "max",
                    _ => "medium"
                };
            }
            else if (model.Reasoning)
            {
                var budgetLevel = SimpleOptionsHelper.GetBudgetForLevel(
                    clamped ?? ThinkingLevel.Medium, options?.ThinkingBudgets);

                int budgetTokens;
                int? maxTokens;

                if (budgetLevel is not null)
                {
                    budgetTokens = budgetLevel.ThinkingBudget;
                    maxTokens = budgetLevel.MaxTokens;
                }
                else
                {
                    budgetTokens = clamped switch
                    {
                        ThinkingLevel.Minimal => 1024,
                        ThinkingLevel.Low => 4096,
                        ThinkingLevel.Medium => 10000,
                        ThinkingLevel.High => 32000,
                        _ => 10000
                    };
                    maxTokens = anthropicOpts.MaxTokens;
                }

                var (adjustedMax, adjustedBudget) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
                    model, maxTokens, budgetTokens);

                anthropicOpts = anthropicOpts with
                {
                    ThinkingEnabled = true,
                    ThinkingBudgetTokens = adjustedBudget,
                    MaxTokens = adjustedMax
                };
            }
        }

        return Stream(model, context, anthropicOpts);
    }

    #region SSE Streaming

    private async Task StreamCoreAsync(
        LlmModel model, Context context, StreamOptions? options,
        LlmStream stream, List<ContentBlock> contentBlocks, Usage initialUsage,
        Action<Usage> setUsage,
        Action<string?> setResponseId, Action<StopReason> setStopReason,
        CancellationToken ct)
    {
        var usage = initialUsage;
        var anthropicOpts = options as AnthropicOptions;
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }
        var baseUrl = model.BaseUrl.TrimEnd('/');
        var authMode = DetectAuthMode(apiKey, model);

        var requestBody = BuildRequestBody(model, context, options, anthropicOpts);

        if (options?.OnPayload is { } onPayload)
        {
            var modified = await onPayload(requestBody, model);
            if (modified is Dictionary<string, object?> modifiedDict)
                requestBody = modifiedDict;
        }

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        ConfigureRequestHeaders(httpRequest, apiKey, authMode, anthropicOpts, model);
        if (authMode == AuthMode.Copilot)
        {
            var transformedMessages = MessageTransformer.TransformMessages(context.Messages, model, NormalizeToolCallId);
            var hasImages = CopilotHeaders.HasVisionInput(transformedMessages);
            foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(transformedMessages, hasImages))
                httpRequest.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode}: {errorBody}");
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

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

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = line[5..].TrimStart();
                if (string.IsNullOrWhiteSpace(data)) continue;

                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(data); }
                catch { continue; }

                using (doc)
                {
                    ProcessSseEvent(currentEvent, doc.RootElement, model, stream,
                        contentBlocks, blockTypes, textAccumulators, signatureAccumulators,
                        toolCallIds, toolCallNames, usage,
                        ref responseId, ref stopReason, out usage);
                }

                currentEvent = null;
            }
        }

        setUsage(usage);
        setResponseId(responseId);
        setStopReason(stopReason);
    }

    private static void ProcessSseEvent(
        string? eventType, JsonElement data, LlmModel model, LlmStream stream,
        List<ContentBlock> contentBlocks, Dictionary<int, string> blockTypes,
        Dictionary<int, StringBuilder> textAccumulators,
        Dictionary<int, StringBuilder> signatureAccumulators,
        Dictionary<int, string> toolCallIds, Dictionary<int, string> toolCallNames,
        Usage usage, ref string? responseId, ref StopReason stopReason, out Usage updatedUsage)
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
                    BuildMessage(model, contentBlocks, updatedUsage, StopReason.Stop, null, responseId)));
                break;

            case "content_block_start":
                HandleContentBlockStart(data, model, stream, contentBlocks, blockTypes,
                    textAccumulators, signatureAccumulators, toolCallIds, toolCallNames, usage, responseId);
                break;

            case "content_block_delta":
                HandleContentBlockDelta(data, model, stream, contentBlocks, blockTypes,
                    textAccumulators, signatureAccumulators, usage, responseId);
                break;

            case "content_block_stop":
                HandleContentBlockStop(data, model, stream, contentBlocks, blockTypes,
                    textAccumulators, signatureAccumulators, toolCallIds, toolCallNames, usage, responseId);
                break;

            case "message_delta":
                if (data.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("stop_reason", out var sr))
                {
                    stopReason = MapStopReason(sr.GetString());
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
        JsonElement data, LlmModel model, LlmStream stream,
        List<ContentBlock> contentBlocks, Dictionary<int, string> blockTypes,
        Dictionary<int, StringBuilder> textAccumulators,
        Dictionary<int, StringBuilder> signatureAccumulators,
        Dictionary<int, string> toolCallIds, Dictionary<int, string> toolCallNames,
        Usage usage, string? responseId)
    {
        var index = data.GetProperty("index").GetInt32();
        if (!data.TryGetProperty("content_block", out var block)) return;

        var blockType = block.GetProperty("type").GetString() ?? "text";
        blockTypes[index] = blockType;
        textAccumulators[index] = new StringBuilder();
        signatureAccumulators[index] = new StringBuilder();

        var partial = BuildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);

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
                    textAccumulators[index].Append(redactedData.GetString());
                stream.Push(new ThinkingStartEvent(index, partial));
                break;
            case "tool_use":
                if (block.TryGetProperty("id", out var tcId))
                    toolCallIds[index] = tcId.GetString() ?? "";
                if (block.TryGetProperty("name", out var tcName))
                    toolCallNames[index] = tcName.GetString() ?? "";
                stream.Push(new ToolCallStartEvent(index, partial));
                break;
        }
    }

    private static void HandleContentBlockDelta(
        JsonElement data, LlmModel model, LlmStream stream,
        List<ContentBlock> contentBlocks, Dictionary<int, string> blockTypes,
        Dictionary<int, StringBuilder> textAccumulators,
        Dictionary<int, StringBuilder> signatureAccumulators,
        Usage usage, string? responseId)
    {
        var index = data.GetProperty("index").GetInt32();
        if (!data.TryGetProperty("delta", out var delta)) return;

        var deltaType = delta.GetProperty("type").GetString();
        var partial = BuildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);

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
        JsonElement data, LlmModel model, LlmStream stream,
        List<ContentBlock> contentBlocks, Dictionary<int, string> blockTypes,
        Dictionary<int, StringBuilder> textAccumulators,
        Dictionary<int, StringBuilder> signatureAccumulators,
        Dictionary<int, string> toolCallIds, Dictionary<int, string> toolCallNames,
        Usage usage, string? responseId)
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
                var textPartial = BuildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new TextEndEvent(index, accumulated, textPartial));
                break;

            case "thinking":
                contentBlocks.Add(new ThinkingContent(accumulated, signature));
                var thinkPartial = BuildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new ThinkingEndEvent(index, accumulated, thinkPartial));
                break;

            case "redacted_thinking":
                contentBlocks.Add(new ThinkingContent(accumulated, signature, Redacted: true));
                var redactPartial = BuildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new ThinkingEndEvent(index, accumulated, redactPartial));
                break;

            case "tool_use":
                var toolId = toolCallIds.GetValueOrDefault(index, "");
                var toolName = toolCallNames.GetValueOrDefault(index, "");
                var args = StreamingJsonParser.Parse(accumulated);
                var toolCall = new ToolCallContent(toolId, toolName, args, signature);
                contentBlocks.Add(toolCall);
                var toolPartial = BuildMessage(model, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new ToolCallEndEvent(index, toolCall, toolPartial));
                break;
        }
    }

    #endregion

    #region Request Building

    private static Dictionary<string, object?> BuildRequestBody(
        LlmModel model, Context context, StreamOptions? options,
        AnthropicOptions? anthropicOpts)
    {
        var messages = ConvertMessages(context.Messages, model);

        var body = new Dictionary<string, object?>
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["max_tokens"] = options?.MaxTokens ?? (model.MaxTokens / 3),
            ["stream"] = true
        };

        // System prompt as array of text blocks with cache control
        if (context.SystemPrompt is { } systemPrompt)
        {
            var cacheControl = BuildCacheControl(
                options?.CacheRetention ?? CacheRetention.Short, model.BaseUrl);

            body["system"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = cacheControl
                }
            };
        }

        // Tools
        if (context.Tools is { Count: > 0 } tools)
        {
            body["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.Parameters
            }).ToList();
        }

        // Tool choice
        if (anthropicOpts?.ToolChoice is { } toolChoice)
        {
            body["tool_choice"] = toolChoice switch
            {
                "auto" => new Dictionary<string, object?> { ["type"] = "auto" },
                "any" => new Dictionary<string, object?> { ["type"] = "any" },
                "none" => new Dictionary<string, object?> { ["type"] = "none" },
                _ => new Dictionary<string, object?> { ["type"] = "tool", ["name"] = toolChoice }
            };
        }

        // Thinking and temperature are mutually exclusive
        if (anthropicOpts?.ThinkingEnabled == true)
        {
            if (anthropicOpts.Effort is not null && IsAdaptiveThinkingModel(model.Id))
            {
                body["thinking"] = new Dictionary<string, object?> { ["type"] = "adaptive" };
                body["output_config"] = new Dictionary<string, object?> { ["effort"] = anthropicOpts.Effort };
            }
            else if (anthropicOpts.ThinkingBudgetTokens is { } budget)
            {
                body["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = budget
                };
            }
            else if (IsAdaptiveThinkingModel(model.Id))
            {
                body["thinking"] = new Dictionary<string, object?> { ["type"] = "adaptive" };
            }
            else
            {
                body["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = 10000
                };
            }
        }
        else
        {
            if (options?.Temperature.HasValue == true)
                body["temperature"] = options.Temperature.Value;
        }

        // Cache control on last user message for conversation history caching
        ApplyLastUserMessageCacheControl(
            messages, options?.CacheRetention ?? CacheRetention.Short, model.BaseUrl);

        return body;
    }

    #endregion

    #region Message Conversion

    private static List<Dictionary<string, object?>> ConvertMessages(
        IReadOnlyList<Message> messages, LlmModel model)
    {
        var transformed = MessageTransformer.TransformMessages(messages, model, NormalizeToolCallId);
        var result = new List<Dictionary<string, object?>>();
        var isLastToolResult = false;

        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case UserMessage user:
                    isLastToolResult = false;
                    result.Add(ConvertUserMessage(user));
                    break;

                case AssistantMessage assistant:
                    isLastToolResult = false;
                    result.Add(ConvertAssistantMessage(assistant));
                    break;

                case ToolResultMessage toolResult:
                    var block = MakeToolResultBlock(toolResult);
                    if (isLastToolResult && result.Count > 0 &&
                        result[^1]["content"] is List<object> existingBlocks)
                    {
                        existingBlocks.Add(block);
                    }
                    else
                    {
                        result.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = new List<object> { block }
                        });
                        isLastToolResult = true;
                    }
                    break;
            }
        }

        return result;
    }

    private static Dictionary<string, object?> ConvertUserMessage(UserMessage msg)
    {
        object content;

        if (msg.Content.IsText)
        {
            content = msg.Content.Text!;
        }
        else
        {
            var blocks = new List<object>();
            foreach (var block in msg.Content.Blocks!)
            {
                switch (block)
                {
                    case TextContent text:
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = text.Text
                        });
                        break;
                    case ImageContent image:
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object?>
                            {
                                ["type"] = "base64",
                                ["media_type"] = image.MimeType,
                                ["data"] = image.Data
                            }
                        });
                        break;
                }
            }
            content = blocks;
        }

        return new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = content
        };
    }

    private static Dictionary<string, object?> ConvertAssistantMessage(AssistantMessage msg)
    {
        var blocks = new List<object>();

        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text:
                    var textBlock = new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = text.Text
                    };
                    if (text.TextSignature is not null)
                        textBlock["signature"] = text.TextSignature;
                    blocks.Add(textBlock);
                    break;

                case ThinkingContent thinking:
                    if (thinking.Redacted == true)
                    {
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = thinking.Thinking
                        });
                    }
                    else
                    {
                        var thinkBlock = new Dictionary<string, object?>
                        {
                            ["type"] = "thinking",
                            ["thinking"] = thinking.Thinking
                        };
                        if (thinking.ThinkingSignature is not null)
                            thinkBlock["signature"] = thinking.ThinkingSignature;
                        blocks.Add(thinkBlock);
                    }
                    break;

                case ToolCallContent toolCall:
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.Id,
                        ["name"] = toolCall.Name,
                        ["input"] = toolCall.Arguments
                    });
                    break;
            }
        }

        return new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = blocks
        };
    }

    private static Dictionary<string, object?> MakeToolResultBlock(ToolResultMessage toolResult)
    {
        object content;

        if (toolResult.Content.Count == 1 && toolResult.Content[0] is TextContent singleText)
        {
            content = singleText.Text;
        }
        else
        {
            content = toolResult.Content.Select<ContentBlock, object>(b => b switch
            {
                TextContent t => new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = t.Text
                },
                ImageContent i => new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["source"] = new Dictionary<string, object?>
                    {
                        ["type"] = "base64",
                        ["media_type"] = i.MimeType,
                        ["data"] = i.Data
                    }
                },
                _ => new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = b.ToString() ?? ""
                }
            }).ToList();
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = toolResult.ToolCallId,
            ["content"] = content,
            ["is_error"] = toolResult.IsError ? true : null
        };
    }

    #endregion

    #region Auth & Headers

    private enum AuthMode { ApiKey, OAuth, Copilot }

    private static AuthMode DetectAuthMode(string? apiKey, LlmModel model)
    {
        if (string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
            return AuthMode.Copilot;
        if (apiKey?.StartsWith("sk-ant-oat", StringComparison.Ordinal) == true)
            return AuthMode.OAuth;
        return AuthMode.ApiKey;
    }

    private static void ConfigureRequestHeaders(
        HttpRequestMessage request, string apiKey, AuthMode authMode,
        AnthropicOptions? opts, LlmModel model)
    {
        request.Headers.Add("anthropic-version", ApiVersion);

        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (opts?.Headers is not null)
        {
            foreach (var (key, value) in opts.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        var betaFeatures = new List<string>();

        if (authMode != AuthMode.Copilot)
            betaFeatures.Add("fine-grained-tool-streaming-2025-05-14");

        if (opts?.InterleavedThinking == true)
            betaFeatures.Add("interleaved-thinking-2025-05-14");

        switch (authMode)
        {
            case AuthMode.ApiKey:
                request.Headers.Add("x-api-key", apiKey);
                break;

            case AuthMode.OAuth:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                betaFeatures.Add("claude-code");
                request.Headers.TryAddWithoutValidation("user-agent", "claude-cli");
                break;

            case AuthMode.Copilot:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
        }

        if (betaFeatures.Count > 0)
            request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", betaFeatures));
    }

    #endregion

    #region Helpers

    private static AssistantMessage BuildMessage(
        LlmModel model, List<ContentBlock> content,
        Usage usage, StopReason stopReason, string? errorMessage, string? responseId)
    {
        var usageWithTotals = usage with
        {
            TotalTokens = usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite
        };
        usageWithTotals = usageWithTotals with
        {
            Cost = ModelRegistry.CalculateCost(model, usageWithTotals)
        };

        return new AssistantMessage(
            Content: [.. content],
            Api: "anthropic-messages",
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: usageWithTotals,
            StopReason: stopReason,
            ErrorMessage: errorMessage,
            ResponseId: responseId,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
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

    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" => StopReason.Stop,
        "max_tokens" => StopReason.Length,
        "tool_use" => StopReason.ToolUse,
        "refusal" => StopReason.Refusal,
        "pause_turn" => StopReason.PauseTurn,
        "stop_sequence" => StopReason.Stop,
        "sensitive" => StopReason.Sensitive,
        _ => throw new InvalidOperationException($"Unhandled Anthropic stop reason: {reason}")
    };

    private static bool IsAdaptiveThinkingModel(string modelId) =>
        modelId.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("sonnet-4.6", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NonAlphanumericRegex();

    private static string NormalizeToolCallId(string id)
    {
        var normalized = NonAlphanumericRegex().Replace(id, "");
        if (normalized.Length > 64)
            normalized = normalized[..64];
        return normalized;
    }

    private static Dictionary<string, object?>? BuildCacheControl(
        CacheRetention retention, string baseUrl)
    {
        if (retention == CacheRetention.None)
            return null;

        var cacheControl = new Dictionary<string, object?> { ["type"] = "ephemeral" };

        if (retention == CacheRetention.Long &&
            baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            cacheControl["ttl"] = 3600;
        }

        return cacheControl;
    }

    private static void ApplyLastUserMessageCacheControl(
        List<Dictionary<string, object?>> messages, CacheRetention retention, string baseUrl)
    {
        if (retention == CacheRetention.None) return;

        var cacheControl = BuildCacheControl(retention, baseUrl);
        if (cacheControl is null) return;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].TryGetValue("role", out var role) && role?.ToString() == "user")
            {
                var content = messages[i]["content"];

                if (content is string textContent)
                {
                    messages[i]["content"] = new List<object>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = textContent,
                            ["cache_control"] = cacheControl
                        }
                    };
                }
                else if (content is List<object> blocks && blocks.Count > 0)
                {
                    if (blocks[^1] is Dictionary<string, object?> lastBlock)
                        lastBlock["cache_control"] = cacheControl;
                }

                break;
            }
        }
    }

    #endregion
}
