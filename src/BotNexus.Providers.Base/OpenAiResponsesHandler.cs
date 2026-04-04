using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Base;

/// <summary>
/// API format handler for OpenAI Responses API.
/// Used by: GPT-5.x models via Copilot.
/// Reference: Pi's openai-responses.ts + openai-responses-shared.ts
/// </summary>
public sealed class OpenAiResponsesHandler : IApiFormatHandler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    
    public OpenAiResponsesHandler(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }
    
    public string ApiFormat => "openai-responses";
    
    public async Task<LlmResponse> ChatAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        CancellationToken cancellationToken)
    {
        // OpenAI Responses API is inherently event-driven/streaming
        // For non-streaming, we aggregate all events into a final response
        var contentBuilder = new StringBuilder();
        var toolCalls = new List<ToolCallRequest>();
        var toolCallBuffers = new Dictionary<string, ToolCallBuffer>();
        FinishReason? finishReason = null;
        int? inputTokens = null;
        int? outputTokens = null;
        
        await foreach (var chunk in ChatStreamAsync(model, request, apiKey, cancellationToken))
        {
            if (chunk.ContentDelta is not null)
                contentBuilder.Append(chunk.ContentDelta);
            
            if (chunk.ToolCallId is not null && chunk.ToolName is not null)
            {
                // New tool call
                if (!toolCallBuffers.ContainsKey(chunk.ToolCallId))
                {
                    toolCallBuffers[chunk.ToolCallId] = new ToolCallBuffer(chunk.ToolCallId, chunk.ToolName);
                }
            }
            
            if (chunk.ToolCallId is not null && chunk.ArgumentsDelta is not null)
            {
                // Accumulate arguments
                if (toolCallBuffers.TryGetValue(chunk.ToolCallId, out var buffer))
                {
                    buffer.ArgumentsBuilder.Append(chunk.ArgumentsDelta);
                }
            }
            
            if (chunk.FinishReason.HasValue)
                finishReason = chunk.FinishReason.Value;
            
            if (chunk.InputTokens.HasValue)
                inputTokens = chunk.InputTokens.Value;
            if (chunk.OutputTokens.HasValue)
                outputTokens = chunk.OutputTokens.Value;
        }
        
        // Parse accumulated tool calls
        foreach (var buffer in toolCallBuffers.Values)
        {
            var argsJson = buffer.ArgumentsBuilder.ToString();
            Dictionary<string, object?> arguments = new();
            
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                try
                {
                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? [];
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse tool call arguments for {ToolName}: {Args}", 
                        buffer.Name, argsJson);
                }
            }
            
            toolCalls.Add(new ToolCallRequest(buffer.Id, buffer.Name, arguments));
        }
        
        _logger.LogInformation("OpenAI Responses response: model={Model}, content_length={ContentLength}, " +
                               "finish_reason={FinishReason}, tool_calls={ToolCallCount}",
            model.Id, contentBuilder.Length, finishReason, toolCalls.Count);
        
        return new LlmResponse(
            contentBuilder.ToString(), 
            finishReason ?? FinishReason.Other, 
            toolCalls.Count > 0 ? toolCalls : null,
            inputTokens,
            outputTokens);
    }
    
    public async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildRequestPayload(model, request);
        
        using var httpRequest = CreateHttpRequest(model, payload, apiKey);
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("OpenAI Responses API returned HTTP {StatusCode} for model {Model}: {Body}", 
                (int)response.StatusCode, model.Id, errorBody);
            response.EnsureSuccessStatusCode();
        }
        
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        
        var toolCallBuffers = new Dictionary<string, ToolCallBuffer>();
        
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            // OpenAI Responses uses SSE format: "event: <type>\ndata: <json>\n"
            if (!line.StartsWith("event: ", StringComparison.Ordinal))
                continue;
            
            var eventType = line["event: ".Length..];
            var dataLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            
            if (dataLine is null || !dataLine.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            
            var data = dataLine["data: ".Length..];
            
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse OpenAI Responses SSE chunk: {Data}", data);
                continue;
            }
            
            using (document)
            {
                switch (eventType)
                {
                    case "response.output_item.added":
                        // New output item (text or function_call)
                        if (document.RootElement.TryGetProperty("item", out var item))
                        {
                            var itemType = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                            if (itemType == "function_call")
                            {
                                var callId = item.TryGetProperty("call_id", out var idEl) ? idEl.GetString() : null;
                                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                                if (!string.IsNullOrEmpty(callId) && !string.IsNullOrEmpty(name))
                                {
                                    toolCallBuffers[callId] = new ToolCallBuffer(callId, name);
                                    yield return StreamingChatChunk.FromToolCallStart(callId, name);
                                }
                            }
                        }
                        break;
                    
                    case "response.content_part.added":
                        // New content part
                        if (document.RootElement.TryGetProperty("part", out var part))
                        {
                            var partType = part.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                            if (partType == "text")
                            {
                                var text = part.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
                                if (!string.IsNullOrEmpty(text))
                                    yield return StreamingChatChunk.FromContentDelta(text);
                            }
                        }
                        break;
                    
                    case "response.text.delta":
                        // Text delta
                        if (document.RootElement.TryGetProperty("delta", out var textDelta))
                        {
                            var text = textDelta.GetString();
                            if (!string.IsNullOrEmpty(text))
                                yield return StreamingChatChunk.FromContentDelta(text);
                        }
                        break;
                    
                    case "response.function_call_arguments.delta":
                        // Function call arguments delta
                        if (document.RootElement.TryGetProperty("call_id", out var callIdEl))
                        {
                            var callId = callIdEl.GetString();
                            if (!string.IsNullOrEmpty(callId) && 
                                document.RootElement.TryGetProperty("delta", out var argsDelta))
                            {
                                var args = argsDelta.GetString();
                                if (!string.IsNullOrEmpty(args))
                                {
                                    if (toolCallBuffers.TryGetValue(callId, out var buffer))
                                    {
                                        buffer.ArgumentsBuilder.Append(args);
                                        yield return StreamingChatChunk.FromToolCallDelta(callId, args);
                                    }
                                }
                            }
                        }
                        break;
                    
                    case "response.done":
                        // End of response with final metadata
                        if (document.RootElement.TryGetProperty("response", out var responseObj))
                        {
                            // Status maps to finish reason
                            var status = responseObj.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                            var finishReason = MapStatusToFinishReason(status);
                            if (finishReason != FinishReason.Other)
                                yield return StreamingChatChunk.FromFinishReason(finishReason);
                            
                            // Usage
                            if (responseObj.TryGetProperty("usage", out var usageEl))
                            {
                                var inputTokens = usageEl.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : (int?)null;
                                var outputTokens = usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : (int?)null;
                                if (inputTokens.HasValue && outputTokens.HasValue)
                                    yield return StreamingChatChunk.FromUsage(inputTokens.Value, outputTokens.Value);
                            }
                        }
                        yield break;
                    
                    case "error":
                        // Error event
                        var errorMsg = document.RootElement.TryGetProperty("error", out var errorEl) 
                            ? errorEl.GetRawText() 
                            : "Unknown error";
                        _logger.LogError("OpenAI Responses API error: {Error}", errorMsg);
                        throw new HttpRequestException($"OpenAI Responses API error: {errorMsg}");
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
    
    private Dictionary<string, object?> BuildRequestPayload(ModelDefinition model, ChatRequest request)
    {
        // Convert messages to OpenAI Responses format
        var messages = new List<Dictionary<string, object?>>();
        
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["role"] = "system",
                ["content"] = new List<Dictionary<string, object?>>
                {
                    new() { ["type"] = "input_text", ["text"] = request.SystemPrompt }
                }
            });
        }
        
        foreach (var message in request.Messages)
        {
            var msg = new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["role"] = message.Role
            };
            
            var contentParts = new List<Dictionary<string, object?>>();
            
            // Text content
            if (!string.IsNullOrEmpty(message.Content))
            {
                contentParts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "input_text",
                    ["text"] = message.Content
                });
            }
            
            // Tool calls (assistant messages) - convert to function_call items
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    contentParts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call",
                        ["call_id"] = toolCall.Id,
                        ["name"] = toolCall.ToolName,
                        ["arguments"] = JsonSerializer.Serialize(toolCall.Arguments)
                    });
                }
            }
            
            // Tool results
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(message.ToolCallId))
            {
                contentParts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = message.ToolCallId,
                    ["output"] = message.Content
                });
            }
            
            msg["content"] = contentParts;
            messages.Add(msg);
        }
        
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.Id,
            ["input"] = messages
        };
        
        // Settings
        var responseConfig = new Dictionary<string, object?>();
        
        if (request.Settings.MaxTokens.HasValue)
            responseConfig["max_output_tokens"] = request.Settings.MaxTokens.Value;
        if (request.Settings.Temperature.HasValue)
            responseConfig["temperature"] = request.Settings.Temperature.Value;
        
        if (responseConfig.Count > 0)
            payload["response"] = responseConfig;
        
        // Tools
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = BuildParameterSchema(tool)
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
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
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
        
        return request;
    }
    
    private static FinishReason MapStatusToFinishReason(string? status) => status switch
    {
        "completed" => FinishReason.Stop,
        "incomplete" => FinishReason.Length,
        "failed" => FinishReason.Other,
        "cancelled" => FinishReason.Other,
        _ => FinishReason.Other
    };
}
