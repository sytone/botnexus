using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Base;

/// <summary>
/// API format handler for OpenAI Chat Completions API.
/// Used by: GPT models, Gemini models, Grok.
/// Reference: Pi's openai-completions.ts
/// </summary>
public sealed class OpenAiCompletionsHandler : IApiFormatHandler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    
    public OpenAiCompletionsHandler(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }
    
    public string ApiFormat => "openai-completions";
    
    public async Task<LlmResponse> ChatAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        CancellationToken cancellationToken)
    {
        var payload = BuildRequestPayload(model, request, stream: false);
        
        using var httpRequest = CreateHttpRequest(model, payload, apiKey);
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("OpenAI Completions API returned HTTP {StatusCode} for model {Model}: {Body}", 
                (int)response.StatusCode, model.Id, errorBody);
            response.EnsureSuccessStatusCode();
        }
        
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("OpenAI Completions response: {ResponseContent}", responseContent);
        
        using var document = JsonDocument.Parse(responseContent);
        var root = document.RootElement;
        
        return ParseResponse(root, model.Id);
    }
    
    public async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildRequestPayload(model, request, stream: true);
        
        using var httpRequest = CreateHttpRequest(model, payload, apiKey);
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("OpenAI Completions streaming returned HTTP {StatusCode}: {Body}", 
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }
        
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        
        var toolCallBuffers = new Dictionary<string, ToolCallBuffer>();
        
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            
            var data = line["data: ".Length..];
            if (data == "[DONE]")
                yield break;
            
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse SSE chunk: {Data}", data);
                continue;
            }
            
            using (document)
            {
                if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    // Check for usage in final chunk
                    if (document.RootElement.TryGetProperty("usage", out var usageElement))
                    {
                        var inputTokens = usageElement.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : (int?)null;
                        var outputTokens = usageElement.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : (int?)null;
                        if (inputTokens.HasValue && outputTokens.HasValue)
                            yield return StreamingChatChunk.FromUsage(inputTokens.Value, outputTokens.Value);
                    }
                    continue;
                }
                
                foreach (var choice in choices.EnumerateArray())
                {
                    var delta = choice.TryGetProperty("delta", out var deltaElement)
                        ? deltaElement
                        : default;
                    
                    if (delta.ValueKind != JsonValueKind.Object)
                        continue;
                    
                    // Content delta
                    if (delta.TryGetProperty("content", out var contentElement) &&
                        contentElement.ValueKind == JsonValueKind.String)
                    {
                        var content = contentElement.GetString();
                        if (!string.IsNullOrEmpty(content))
                            yield return StreamingChatChunk.FromContentDelta(content);
                    }
                    
                    // Tool calls delta
                    if (delta.TryGetProperty("tool_calls", out var toolCallsElement) &&
                        toolCallsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var toolCallDelta in toolCallsElement.EnumerateArray())
                        {
                            var index = toolCallDelta.TryGetProperty("index", out var indexEl) ? indexEl.GetInt32() : 0;
                            var id = toolCallDelta.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            
                            if (toolCallDelta.TryGetProperty("function", out var functionEl))
                            {
                                var name = functionEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                                var argumentsDelta = functionEl.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() : null;
                                
                                // Start of new tool call
                                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                                {
                                    toolCallBuffers[id] = new ToolCallBuffer(id, name);
                                    yield return StreamingChatChunk.FromToolCallStart(id, name);
                                }
                                
                                // Arguments delta
                                if (!string.IsNullOrEmpty(argumentsDelta))
                                {
                                    string? targetId = id;
                                    if (string.IsNullOrEmpty(targetId))
                                    {
                                        targetId = toolCallBuffers.Values.ElementAtOrDefault(index)?.Id;
                                    }
                                    
                                    if (!string.IsNullOrEmpty(targetId) && toolCallBuffers.ContainsKey(targetId))
                                    {
                                        toolCallBuffers[targetId].ArgumentsBuilder.Append(argumentsDelta);
                                        yield return StreamingChatChunk.FromToolCallDelta(targetId, argumentsDelta);
                                    }
                                }
                            }
                        }
                    }
                    
                    // Finish reason
                    if (choice.TryGetProperty("finish_reason", out var finishReasonElement) &&
                        finishReasonElement.ValueKind == JsonValueKind.String)
                    {
                        var finishReasonStr = finishReasonElement.GetString();
                        if (!string.IsNullOrEmpty(finishReasonStr))
                        {
                            var finishReason = MapFinishReason(finishReasonStr);
                            yield return StreamingChatChunk.FromFinishReason(finishReason);
                        }
                    }
                }
            }
        }
    }
    
    private sealed class ToolCallBuffer
    {
        public string Id { get; }
        public string Name { get; }
        public StringBuilder ArgumentsBuilder { get; } = new();
        
        public ToolCallBuffer(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }
    
    private LlmResponse ParseResponse(JsonElement root, string modelId)
    {
        var choices = root.GetProperty("choices");
        
        // Multi-choice merging: collect content and tool_calls from all choices
        string content = string.Empty;
        string? finishReasonStr = null;
        JsonElement? toolCallsElement = null;
        
        foreach (var choice in choices.EnumerateArray())
        {
            var msg = choice.GetProperty("message");
            
            if (string.IsNullOrEmpty(content) && msg.TryGetProperty("content", out var contentEl))
            {
                var text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    content = text;
            }
            
            if (toolCallsElement is null && msg.TryGetProperty("tool_calls", out var tcEl) && 
                tcEl.ValueKind == JsonValueKind.Array && tcEl.GetArrayLength() > 0)
                toolCallsElement = tcEl;
            
            if (finishReasonStr is null && choice.TryGetProperty("finish_reason", out var frEl))
                finishReasonStr = frEl.GetString();
        }
        
        var finishReason = MapFinishReason(finishReasonStr);
        
        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        if (toolCallsElement is not null)
        {
            toolCalls = ParseToolCalls(toolCallsElement.Value);
        }
        
        // Token counts
        int? inputTokens = null;
        int? outputTokens = null;
        if (root.TryGetProperty("usage", out var usageElement))
        {
            if (usageElement.TryGetProperty("prompt_tokens", out var pt))
                inputTokens = pt.GetInt32();
            if (usageElement.TryGetProperty("completion_tokens", out var ct))
                outputTokens = ct.GetInt32();
        }
        
        _logger.LogInformation("OpenAI Completions response: model={Model}, content_length={ContentLength}, " +
                               "finish_reason={FinishReason}, tool_calls={ToolCallCount}",
            modelId, content?.Length ?? 0, finishReason, toolCalls?.Count ?? 0);
        
        return new LlmResponse(content ?? string.Empty, finishReason, toolCalls, inputTokens, outputTokens);
    }
    
    private IReadOnlyList<ToolCallRequest> ParseToolCalls(JsonElement toolCallsElement)
    {
        var result = new List<ToolCallRequest>();
        
        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            var id = toolCall.TryGetProperty("id", out var idElement)
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
            
            if (!toolCall.TryGetProperty("function", out var functionElement))
            {
                _logger.LogWarning("Tool call missing 'function' property: {RawToolCall}", toolCall.GetRawText());
                continue;
            }
            
            var name = functionElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            
            // Handle dual argument format: string or object
            Dictionary<string, object?> arguments;
            if (functionElement.TryGetProperty("arguments", out var argumentsElement))
            {
                if (argumentsElement.ValueKind == JsonValueKind.String)
                {
                    var argumentsJson = argumentsElement.GetString();
                    arguments = string.IsNullOrWhiteSpace(argumentsJson)
                        ? new Dictionary<string, object?>()
                        : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];
                }
                else if (argumentsElement.ValueKind == JsonValueKind.Object)
                {
                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsElement.GetRawText()) ?? [];
                }
                else
                {
                    _logger.LogWarning("Unexpected arguments type {ValueKind} for tool {ToolName}, using empty dict", 
                        argumentsElement.ValueKind, name);
                    arguments = new Dictionary<string, object?>();
                }
            }
            else
            {
                arguments = new Dictionary<string, object?>();
            }
            
            result.Add(new ToolCallRequest(id, name, arguments));
        }
        
        return result;
    }
    
    private Dictionary<string, object?> BuildRequestPayload(ModelDefinition model, ChatRequest request, bool stream)
    {
        var messages = new List<Dictionary<string, object?>>();
        
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }
        
        foreach (var message in request.Messages)
        {
            var msg = new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            };
            
            // Assistant messages with tool_calls
            if (message.ToolCalls is { Count: > 0 })
            {
                msg["tool_calls"] = message.ToolCalls.Select(tc => new Dictionary<string, object?>
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tc.ToolName,
                        ["arguments"] = JsonSerializer.Serialize(tc.Arguments)
                    }
                }).ToList();
            }
            
            // Tool result messages
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(message.ToolCallId))
                    msg["tool_call_id"] = message.ToolCallId;
                if (!string.IsNullOrEmpty(message.ToolName))
                    msg["name"] = message.ToolName;
            }
            
            messages.Add(msg);
        }
        
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["stream"] = stream
        };
        
        // Only include optional parameters if explicitly set
        if (request.Settings.MaxTokens.HasValue)
            payload["max_tokens"] = request.Settings.MaxTokens.Value;
        if (request.Settings.Temperature.HasValue)
            payload["temperature"] = request.Settings.Temperature.Value;
        
        // Tools
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = BuildParameterSchema(tool)
                }
            }).ToList();
        }
        
        return payload;
    }
    
    private static Dictionary<string, object?> BuildParameterSchema(ToolDefinition tool)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var required = new List<string>();
        
        foreach (var parameter in tool.Parameters)
        {
            var schema = new Dictionary<string, object?>
            {
                ["type"] = parameter.Value.Type,
                ["description"] = parameter.Value.Description
            };
            if (parameter.Value.EnumValues is { Count: > 0 })
                schema["enum"] = parameter.Value.EnumValues;
            if (parameter.Value.Items is not null)
            {
                schema["items"] = new Dictionary<string, object?>
                {
                    ["type"] = parameter.Value.Items.Type,
                    ["description"] = parameter.Value.Items.Description
                };
            }
            
            properties[parameter.Key] = schema;
            if (parameter.Value.Required)
                required.Add(parameter.Key);
        }
        
        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }
    
    private HttpRequestMessage CreateHttpRequest(ModelDefinition model, Dictionary<string, object?> payload, string apiKey)
    {
        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        
        // Add model-specific headers
        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
        
        // Add X-Initiator header
        request.Headers.TryAddWithoutValidation("X-Initiator", "agent");
        request.Headers.TryAddWithoutValidation("Openai-Intent", "conversation-panel");
        
        // Vision header if needed
        var hasVision = payload.TryGetValue("messages", out var messagesObj) && 
                        messagesObj is IEnumerable<object> messages &&
                        messages.Any(m => m is Dictionary<string, object?> msg && 
                                         msg.TryGetValue("content", out var content) && 
                                         content is IEnumerable<object> contentParts &&
                                         contentParts.Any(p => p is Dictionary<string, object?> part && 
                                                              part.TryGetValue("type", out var type) && 
                                                              type?.ToString() == "image_url"));
        if (hasVision)
        {
            request.Headers.TryAddWithoutValidation("Copilot-Vision-Request", "true");
        }
        
        return request;
    }
    
    private static FinishReason MapFinishReason(string? reason) => reason switch
    {
        "stop" => FinishReason.Stop,
        "tool_calls" => FinishReason.ToolCalls,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Other
    };
}
