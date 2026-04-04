using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using BotNexus.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Providers.Copilot;

/// <summary>
/// GitHub Copilot API provider. Routes requests through Copilot's proxy
/// using the OpenAI Completions format with Copilot-specific auth and headers.
/// </summary>
public sealed class CopilotProvider : IApiProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public string Api => "github-copilot";

    public CopilotProvider(HttpClient? httpClient = null, ILogger<CopilotProvider>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<CopilotProvider>.Instance;
    }

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        _ = StreamCoreAsync(model, context, options ?? new StreamOptions(), stream);
        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var baseOptions = SimpleOptionsHelper.BuildBaseOptions(
            model, options,
            options?.ApiKey ?? EnvironmentApiKeys.GetApiKey("github-copilot") ?? "");
        return Stream(model, context, baseOptions);
    }

    private async Task StreamCoreAsync(LlmModel model, Context context, StreamOptions options, LlmStream stream)
    {
        var ct = options.CancellationToken;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        try
        {
            var apiKey = options.ApiKey
                ?? EnvironmentApiKeys.GetApiKey("github-copilot")
                ?? throw new InvalidOperationException(
                    "No API key for github-copilot. Set COPILOT_GITHUB_TOKEN, GH_TOKEN, or GITHUB_TOKEN, or use CopilotOAuth.");

            var payload = BuildPayload(model, context, options);

            if (options.OnPayload is not null)
            {
                var transformed = await options.OnPayload(payload, model);
                if (transformed is JsonObject obj)
                    payload = obj;
            }

            var url = $"{model.BaseUrl.TrimEnd('/')}/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            // Auth
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            request.Headers.TryAddWithoutValidation("Openai-Intent", "conversation-edits");
            request.Headers.TryAddWithoutValidation("User-Agent", "BotNexus/0.1");

            // Copilot dynamic headers
            var hasImages = CopilotHeaders.HasVisionInput(context.Messages);
            var dynamicHeaders = CopilotHeaders.BuildDynamicHeaders(context.Messages, hasImages);
            foreach (var (key, value) in dynamicHeaders)
                request.Headers.TryAddWithoutValidation(key, value);

            // Model-level headers
            if (model.Headers is not null)
                foreach (var (key, value) in model.Headers)
                    request.Headers.TryAddWithoutValidation(key, value);

            // Custom headers from options
            if (options.Headers is not null)
                foreach (var (key, value) in options.Headers)
                    request.Headers.TryAddWithoutValidation(key, value);

            _logger.LogDebug("Copilot streaming {Model} via {Url}", model.Id, url);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Copilot HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
                var error = MakeMessage(model, timestamp, [], Usage.Empty(), StopReason.Error,
                    $"HTTP {(int)response.StatusCode}: {body}");
                stream.Push(new ErrorEvent(StopReason.Error, error));
                stream.End();
                return;
            }

            await ParseSseAsync(model, response, stream, timestamp, ct);
        }
        catch (OperationCanceledException)
        {
            var msg = MakeMessage(model, timestamp, [], Usage.Empty(), StopReason.Aborted, "Request cancelled");
            stream.Push(new ErrorEvent(StopReason.Aborted, msg));
            stream.End();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Copilot stream error for {Model}", model.Id);
            var msg = MakeMessage(model, timestamp, [], Usage.Empty(), StopReason.Error, ex.Message);
            stream.Push(new ErrorEvent(StopReason.Error, msg));
            stream.End();
        }
    }

    #region Payload Building (OpenAI Completions Format)

    private static JsonObject BuildPayload(LlmModel model, Context context, StreamOptions options)
    {
        var messages = new JsonArray();

        // System prompt
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            var role = model.Compat?.SupportsDeveloperRole is true ? "developer" : "system";
            messages.Add(new JsonObject { ["role"] = role, ["content"] = context.SystemPrompt });
        }

        // Messages — transform for cross-provider compatibility
        var transformed = MessageTransformer.TransformMessages(context.Messages, model);
        foreach (var msg in transformed)
        {
            var node = msg switch
            {
                UserMessage user => BuildUserMessage(user),
                AssistantMessage assistant => BuildAssistantMessage(assistant),
                ToolResultMessage toolResult => BuildToolResultMessage(toolResult),
                _ => null
            };
            if (node is not null)
                messages.Add(node);
        }

        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };

        if (options.Temperature.HasValue)
            payload["temperature"] = options.Temperature.Value;

        var maxTokensField = model.Compat?.MaxTokensField ?? "max_completion_tokens";
        if (options.MaxTokens.HasValue)
            payload[maxTokensField] = options.MaxTokens.Value;

        if (context.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in context.Tools)
            {
                var fn = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
                };
                if (model.Compat?.SupportsStrictMode is true)
                    fn["strict"] = true;

                tools.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
            }
            payload["tools"] = tools;
        }

        if (model.Compat?.SupportsStore is true && options.SessionId is not null)
        {
            payload["store"] = true;
            payload["metadata"] = new JsonObject { ["session_id"] = options.SessionId };
        }

        return payload;
    }

    private static JsonObject BuildUserMessage(UserMessage user)
    {
        if (user.Content.IsText)
            return new JsonObject { ["role"] = "user", ["content"] = user.Content.Text };

        var parts = new JsonArray();
        if (user.Content.Blocks is not null)
        {
            foreach (var block in user.Content.Blocks)
            {
                switch (block)
                {
                    case TextContent tc:
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = tc.Text });
                        break;
                    case ImageContent ic:
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = $"data:{ic.MimeType};base64,{ic.Data}"
                            }
                        });
                        break;
                }
            }
        }

        return new JsonObject { ["role"] = "user", ["content"] = parts };
    }

    private static JsonObject BuildAssistantMessage(AssistantMessage assistant)
    {
        var msg = new JsonObject { ["role"] = "assistant" };

        var textParts = assistant.Content.OfType<TextContent>().ToList();
        if (textParts.Count > 0)
            msg["content"] = string.Join("", textParts.Select(t => t.Text));

        var toolCalls = assistant.Content.OfType<ToolCallContent>().ToList();
        if (toolCalls.Count > 0)
        {
            var tcArray = new JsonArray();
            foreach (var tc in toolCalls)
            {
                tcArray.Add(new JsonObject
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = JsonSerializer.Serialize(tc.Arguments)
                    }
                });
            }
            msg["tool_calls"] = tcArray;
        }

        return msg;
    }

    private static JsonObject BuildToolResultMessage(ToolResultMessage toolResult)
    {
        var content = string.Join("\n", toolResult.Content.Select(b => b switch
        {
            TextContent tc => tc.Text,
            _ => ""
        }));

        return new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = toolResult.ToolCallId,
            ["content"] = content
        };
    }

    #endregion

    #region SSE Response Parsing

    private async Task ParseSseAsync(
        LlmModel model, HttpResponseMessage response, LlmStream stream,
        long timestamp, CancellationToken ct)
    {
        using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8);

        var contentBlocks = new List<ContentBlock>();
        var usage = Usage.Empty();
        var textBuilder = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallAccumulator>();
        var textStarted = false;
        var nextContentIndex = 0;
        string? responseId = null;

        var partial = MakeMessage(model, timestamp, contentBlocks, usage, StopReason.Stop);
        stream.Push(new StartEvent(partial));

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line[6..];
            if (data is "[DONE]")
                break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                responseId ??= root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                // Usage (often in the final chunk before [DONE])
                if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    usage = ParseUsage(usageEl);

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;

                var choice = choices[0];
                var finishReason = choice.TryGetProperty("finish_reason", out var frEl) &&
                                   frEl.ValueKind == JsonValueKind.String
                    ? frEl.GetString()
                    : null;

                if (choice.TryGetProperty("delta", out var delta))
                {
                    ProcessTextDelta(delta, model, timestamp, contentBlocks, usage, responseId,
                        textBuilder, ref textStarted, nextContentIndex, stream);

                    ProcessToolCallDeltas(delta, model, timestamp, contentBlocks, usage, responseId,
                        textBuilder, ref textStarted, ref nextContentIndex, toolCalls, stream);
                }

                if (finishReason is not null)
                {
                    FinishStream(model, timestamp, contentBlocks, usage, responseId,
                        textBuilder, textStarted, ref nextContentIndex, toolCalls, finishReason, stream);
                    return;
                }
            }
        }

        // Stream ended without explicit finish_reason
        CompleteStream(model, timestamp, contentBlocks, usage, responseId,
            textBuilder, textStarted, toolCalls, stream);
    }

    private static void ProcessTextDelta(
        JsonElement delta, LlmModel model, long timestamp,
        List<ContentBlock> contentBlocks, Usage usage, string? responseId,
        StringBuilder textBuilder, ref bool textStarted, int contentIndex,
        LlmStream stream)
    {
        if (!delta.TryGetProperty("content", out var contentEl) ||
            contentEl.ValueKind != JsonValueKind.String)
            return;

        var text = UnicodeSanitizer.SanitizeSurrogates(contentEl.GetString() ?? "");

        if (!textStarted)
        {
            textStarted = true;
            var partial = MakeMessage(model, timestamp, contentBlocks, usage, StopReason.Stop, null, responseId);
            stream.Push(new TextStartEvent(contentIndex, partial));
        }

        textBuilder.Append(text);
        var snapshot = new List<ContentBlock>(contentBlocks) { new TextContent(textBuilder.ToString()) };
        var updated = MakeMessage(model, timestamp, snapshot, usage, StopReason.Stop, null, responseId);
        stream.Push(new TextDeltaEvent(contentIndex, text, updated));
    }

    private static void ProcessToolCallDeltas(
        JsonElement delta, LlmModel model, long timestamp,
        List<ContentBlock> contentBlocks, Usage usage, string? responseId,
        StringBuilder textBuilder, ref bool textStarted, ref int nextContentIndex,
        Dictionary<int, ToolCallAccumulator> toolCalls, LlmStream stream)
    {
        if (!delta.TryGetProperty("tool_calls", out var tcArrayEl))
            return;

        foreach (var tcEl in tcArrayEl.EnumerateArray())
        {
            var tcIndex = tcEl.GetProperty("index").GetInt32();

            if (!toolCalls.TryGetValue(tcIndex, out var acc))
            {
                // Close text block if open
                if (textStarted)
                {
                    var finalText = textBuilder.ToString();
                    contentBlocks.Add(new TextContent(finalText));
                    var endPartial = MakeMessage(model, timestamp, contentBlocks, usage, StopReason.Stop, null, responseId);
                    stream.Push(new TextEndEvent(nextContentIndex, finalText, endPartial));
                    nextContentIndex++;
                    textStarted = false;
                    textBuilder.Clear();
                }

                acc = new ToolCallAccumulator { ContentIndex = nextContentIndex + tcIndex };
                toolCalls[tcIndex] = acc;

                if (tcEl.TryGetProperty("id", out var tcIdEl))
                    acc.Id = tcIdEl.GetString() ?? $"call_{Guid.NewGuid():N}";
                if (tcEl.TryGetProperty("function", out var fnEl) &&
                    fnEl.TryGetProperty("name", out var nameEl))
                    acc.Name = nameEl.GetString() ?? "";

                var startPartial = MakeMessage(model, timestamp, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new ToolCallStartEvent(acc.ContentIndex, startPartial));
            }

            if (tcEl.TryGetProperty("function", out var funcEl) &&
                funcEl.TryGetProperty("arguments", out var argsEl) &&
                argsEl.ValueKind == JsonValueKind.String)
            {
                var argsDelta = argsEl.GetString() ?? "";
                acc.ArgumentsBuilder.Append(argsDelta);
                var deltaPartial = MakeMessage(model, timestamp, contentBlocks, usage, StopReason.Stop, null, responseId);
                stream.Push(new ToolCallDeltaEvent(acc.ContentIndex, argsDelta, deltaPartial));
            }
        }
    }

    private static void FinishStream(
        LlmModel model, long timestamp,
        List<ContentBlock> contentBlocks, Usage usage, string? responseId,
        StringBuilder textBuilder, bool textStarted, ref int nextContentIndex,
        Dictionary<int, ToolCallAccumulator> toolCalls, string finishReason,
        LlmStream stream)
    {
        if (textStarted)
        {
            var finalText = textBuilder.ToString();
            contentBlocks.Add(new TextContent(finalText));
            var endPartial = MakeMessage(model, timestamp, contentBlocks, usage, StopReason.Stop, null, responseId);
            stream.Push(new TextEndEvent(nextContentIndex, finalText, endPartial));
            nextContentIndex++;
        }

        foreach (var (_, acc) in toolCalls.OrderBy(kvp => kvp.Key))
        {
            var args = StreamingJsonParser.Parse(acc.ArgumentsBuilder.ToString());
            var tc = new ToolCallContent(acc.Id, acc.Name, args);
            contentBlocks.Add(tc);
            var tcPartial = MakeMessage(model, timestamp, contentBlocks, usage, StopReason.Stop, null, responseId);
            stream.Push(new ToolCallEndEvent(acc.ContentIndex, tc, tcPartial));
        }

        var stopReason = finishReason switch
        {
            "stop" => StopReason.Stop,
            "length" => StopReason.Length,
            "tool_calls" => StopReason.ToolUse,
            _ => StopReason.Stop
        };

        usage.Cost = ModelRegistry.CalculateCost(model, usage);
        var result = MakeMessage(model, timestamp, contentBlocks, usage, stopReason, null, responseId);
        stream.Push(new DoneEvent(stopReason, result));
        stream.End(result);
    }

    private static void CompleteStream(
        LlmModel model, long timestamp,
        List<ContentBlock> contentBlocks, Usage usage, string? responseId,
        StringBuilder textBuilder, bool textStarted,
        Dictionary<int, ToolCallAccumulator> toolCalls, LlmStream stream)
    {
        if (textStarted)
            contentBlocks.Add(new TextContent(textBuilder.ToString()));

        foreach (var (_, acc) in toolCalls.OrderBy(kvp => kvp.Key))
        {
            var args = StreamingJsonParser.Parse(acc.ArgumentsBuilder.ToString());
            contentBlocks.Add(new ToolCallContent(acc.Id, acc.Name, args));
        }

        usage.Cost = ModelRegistry.CalculateCost(model, usage);
        var result = MakeMessage(model, timestamp, contentBlocks, usage, StopReason.Stop, null, responseId);
        stream.Push(new DoneEvent(StopReason.Stop, result));
        stream.End(result);
    }

    private static Usage ParseUsage(JsonElement el)
    {
        var usage = new Usage
        {
            Input = el.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
            Output = el.TryGetProperty("completion_tokens", out var comp) ? comp.GetInt32() : 0,
            TotalTokens = el.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : 0,
        };

        if (el.TryGetProperty("prompt_tokens_details", out var ptd) &&
            ptd.TryGetProperty("cached_tokens", out var cached))
            usage.CacheRead = cached.GetInt32();

        return usage;
    }

    #endregion

    private static AssistantMessage MakeMessage(
        LlmModel model, long timestamp,
        IReadOnlyList<ContentBlock> content, Usage usage,
        StopReason stopReason, string? errorMessage = null, string? responseId = null)
    {
        return new AssistantMessage(
            Content: content,
            Api: model.Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: usage,
            StopReason: stopReason,
            ErrorMessage: errorMessage,
            ResponseId: responseId,
            Timestamp: timestamp);
    }

    private sealed class ToolCallAccumulator
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int ContentIndex { get; set; }
        public StringBuilder ArgumentsBuilder { get; } = new();
    }
}
