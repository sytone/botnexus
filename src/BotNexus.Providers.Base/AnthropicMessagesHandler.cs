using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Base;

/// <summary>
/// API format handler for Anthropic Messages API.
/// Used by: Claude models via Copilot.
/// Reference: Pi's anthropic.ts
/// </summary>
public sealed class AnthropicMessagesHandler : IApiFormatHandler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    
    public AnthropicMessagesHandler(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }
    
    public string ApiFormat => "anthropic-messages";
    
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
            _logger.LogWarning("Anthropic Messages API returned HTTP {StatusCode} for model {Model}: {Body}", 
                (int)response.StatusCode, model.Id, errorBody);
            response.EnsureSuccessStatusCode();
        }
        
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Anthropic Messages response: {ResponseContent}", responseContent);
        
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
            _logger.LogWarning("Anthropic Messages streaming returned HTTP {StatusCode}: {Body}", 
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
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            // Anthropic SSE format: "event: <type>\ndata: <json>\n"
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
                _logger.LogWarning(ex, "Failed to parse Anthropic SSE chunk: {Data}", data);
                continue;
            }
            
            using (document)
            {
                switch (eventType)
                {
                    case "content_block_start":
                        // New content block (text or tool_use)
                        if (document.RootElement.TryGetProperty("content_block", out var blockStart))
                        {
                            var blockType = blockStart.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                            if (blockType == "tool_use")
                            {
                                var id = blockStart.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                                var name = blockStart.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                                {
                                    toolCallBuffers[id] = new ToolCallBuffer(id, name);
                                    yield return StreamingChatChunk.FromToolCallStart(id, name);
                                }
                            }
                        }
                        break;
                    
                    case "content_block_delta":
                        // Content delta (text or tool input)
                        if (document.RootElement.TryGetProperty("delta", out var delta))
                        {
                            var deltaType = delta.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                            
                            if (deltaType == "text_delta")
                            {
                                var text = delta.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
                                if (!string.IsNullOrEmpty(text))
                                    yield return StreamingChatChunk.FromContentDelta(text);
                            }
                            else if (deltaType == "input_json_delta")
                            {
                                var partialJson = delta.TryGetProperty("partial_json", out var jsonEl) ? jsonEl.GetString() : null;
                                if (!string.IsNullOrEmpty(partialJson))
                                {
                                    // Get the index to find which tool call this belongs to
                                    var index = document.RootElement.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                                    var buffer = toolCallBuffers.Values.ElementAtOrDefault(index);
                                    if (buffer is not null)
                                    {
                                        buffer.ArgumentsBuilder.Append(partialJson);
                                        yield return StreamingChatChunk.FromToolCallDelta(buffer.Id, partialJson);
                                    }
                                }
                            }
                        }
                        break;
                    
                    case "message_delta":
                        // Finish reason and usage
                        if (document.RootElement.TryGetProperty("delta", out var msgDelta))
                        {
                            if (msgDelta.TryGetProperty("stop_reason", out var stopReasonEl))
                            {
                                var stopReason = stopReasonEl.GetString();
                                if (!string.IsNullOrEmpty(stopReason))
                                {
                                    var finishReason = MapFinishReason(stopReason);
                                    yield return StreamingChatChunk.FromFinishReason(finishReason);
                                }
                            }
                        }
                        
                        if (document.RootElement.TryGetProperty("usage", out var usageEl))
                        {
                            var outputTokens = usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : (int?)null;
                            if (outputTokens.HasValue)
                            {
                                // Input tokens come in message_start event, we don't have them here
                                // Yield a partial usage update
                                yield return new StreamingChatChunk { OutputTokens = outputTokens };
                            }
                        }
                        break;
                    
                    case "message_stop":
                        // End of stream
                        yield break;
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
        // Anthropic format: { content: [...], stop_reason: "...", usage: {...} }
        var contentArray = root.GetProperty("content");
        
        var textContent = new StringBuilder();
        var toolCalls = new List<ToolCallRequest>();
        
        foreach (var block in contentArray.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            
            if (blockType == "text")
            {
                var text = block.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
                if (!string.IsNullOrEmpty(text))
                    textContent.Append(text);
            }
            else if (blockType == "tool_use")
            {
                var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                
                Dictionary<string, object?> arguments = new();
                if (block.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Object)
                {
                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(inputEl.GetRawText()) ?? [];
                }
                
                toolCalls.Add(new ToolCallRequest(id, name, arguments));
            }
        }
        
        // Finish reason
        var stopReason = root.TryGetProperty("stop_reason", out var srEl) ? srEl.GetString() : null;
        var finishReason = MapFinishReason(stopReason);
        
        // Token counts
        int? inputTokens = null;
        int? outputTokens = null;
        if (root.TryGetProperty("usage", out var usageElement))
        {
            if (usageElement.TryGetProperty("input_tokens", out var it))
                inputTokens = it.GetInt32();
            if (usageElement.TryGetProperty("output_tokens", out var ot))
                outputTokens = ot.GetInt32();
        }
        
        _logger.LogInformation("Anthropic Messages response: model={Model}, content_length={ContentLength}, " +
                               "finish_reason={FinishReason}, tool_calls={ToolCallCount}",
            modelId, textContent.Length, finishReason, toolCalls.Count);
        
        return new LlmResponse(
            textContent.ToString(), 
            finishReason, 
            toolCalls.Count > 0 ? toolCalls : null, 
            inputTokens, 
            outputTokens);
    }
    
    private Dictionary<string, object?> BuildRequestPayload(ModelDefinition model, ChatRequest request, bool stream)
    {
        // Convert messages to Anthropic format
        var messages = new List<Dictionary<string, object?>>();
        
        foreach (var message in request.Messages)
        {
            // Skip system messages (handled separately)
            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;
            
            var msg = new Dictionary<string, object?>();
            
            // Role mapping (Anthropic uses "user" and "assistant" only)
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                msg["role"] = "user";
            }
            else
            {
                msg["role"] = message.Role;
            }
            
            // Content as content blocks
            var contentBlocks = new List<Dictionary<string, object?>>();
            
            // Text content
            if (!string.IsNullOrEmpty(message.Content))
            {
                contentBlocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = message.Content
                });
            }
            
            // Tool calls (assistant messages)
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    contentBlocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.Id,
                        ["name"] = toolCall.ToolName,
                        ["input"] = toolCall.Arguments
                    });
                }
            }
            
            // Tool results (user messages)
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(message.ToolCallId))
            {
                contentBlocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = message.ToolCallId,
                    ["content"] = message.Content
                });
            }
            
            msg["content"] = contentBlocks;
            messages.Add(msg);
        }
        
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["stream"] = stream
        };
        
        // System prompt (required to be at top level for Anthropic)
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            payload["system"] = request.SystemPrompt;
        }
        
        // max_tokens is REQUIRED by Anthropic API
        payload["max_tokens"] = request.Settings.MaxTokens ?? model.MaxTokens;
        
        // Temperature is optional
        if (request.Settings.Temperature.HasValue)
            payload["temperature"] = request.Settings.Temperature.Value;
        
        // Tools
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(tool => new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = BuildParameterSchema(tool)
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl}/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        
        // Anthropic-specific headers
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        
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
    
    private static FinishReason MapFinishReason(string? reason) => reason switch
    {
        "end_turn" => FinishReason.Stop,
        "tool_use" => FinishReason.ToolCalls,
        "max_tokens" => FinishReason.Length,
        "stop_sequence" => FinishReason.Stop,
        _ => FinishReason.Other
    };
}
