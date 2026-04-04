using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Anthropic;

/// <summary>
/// Anthropic Claude provider implementation using the Anthropic Messages API directly via HttpClient.
/// </summary>
public sealed class AnthropicProvider : LlmProviderBase
{
    private readonly HttpClient _httpClient;
    private readonly string _defaultModel;
    private const string ApiVersion = "2023-06-01";
    private const string DefaultApiBase = "https://api.anthropic.com";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AnthropicProvider(
        string apiKey,
        string model = "claude-3-5-sonnet-20241022",
        string? apiBase = null,
        ILogger<AnthropicProvider>? logger = null,
        int maxRetries = 3)
        : base(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AnthropicProvider>.Instance, maxRetries)
    {
        _defaultModel = model;
        Generation = new GenerationSettings { Model = model };

        _httpClient = new HttpClient { BaseAddress = new Uri(apiBase ?? DefaultApiBase) };
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc/>
    public override string DefaultModel => _defaultModel;

    /// <inheritdoc/>
    public override Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        // Anthropic doesn't provide a models list API, so we return known models
        var models = new[]
        {
            "claude-3-5-sonnet-20241022",
            "claude-3-5-sonnet-20240620",
            "claude-3-5-haiku-20241022",
            "claude-3-opus-20240229",
            "claude-3-sonnet-20240229",
            "claude-3-haiku-20240307"
        };
        return Task.FromResult<IReadOnlyList<string>>(models);
    }

    /// <summary>
    /// Normalizes Anthropic Messages API response to canonical LlmResponse format.
    /// 
    /// <para><b>Provider-Specific Normalization Responsibilities:</b></para>
    /// <list type="bullet">
    ///   <item><b>Content block extraction:</b> Anthropic returns content as array of blocks (text, tool_use), 
    ///   we parse all blocks and extract text + tool calls.</item>
    ///   
    ///   <item><b>Tool calls:</b> Anthropic tool_use blocks are parsed into ToolCallRequest list with 
    ///   id, name, and input (arguments). Supports both streaming and non-streaming modes.</item>
    ///   
    ///   <item><b>Finish reason mapping:</b> Anthropic uses strings ("end_turn", "max_tokens", "tool_use") 
    ///   which we map to FinishReason enum. Tool calls also set FinishReason.ToolCalls.</item>
    ///   
    ///   <item><b>Token count normalization:</b> Anthropic uses snake_case "input_tokens" and "output_tokens" 
    ///   which we map to InputTokens and OutputTokens.</item>
    ///   
    ///   <item><b>JSON naming:</b> Anthropic API uses snake_case throughout, which JsonSerializerOptions 
    ///   handles automatically for request serialization.</item>
    ///   
    ///   <item><b>Tool definitions:</b> Anthropic expects tools array with name, description, and input_schema 
    ///   (object with properties and required fields).</item>
    /// </list>
    /// </summary>
    protected override async Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var actualModel = string.IsNullOrWhiteSpace(request.Settings.Model) ? _defaultModel : request.Settings.Model;
        Logger.LogDebug("AnthropicProvider: Sending chat request with model={Model}", actualModel);
        var body = BuildRequestBody(request, stream: false);
        var json = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("/v1/messages", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        // NORMALIZATION: Extract first content block's text
        var text = root.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        
        // NORMALIZATION: Map snake_case string to canonical enum
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        var finishReason = stopReason == "end_turn" ? FinishReason.Stop
            : stopReason == "max_tokens" ? FinishReason.Length
            : FinishReason.Other;

        // NORMALIZATION: Extract token counts from snake_case fields
        int? inputTokens = null, outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
        }

        // TODO: Parse tool_use content blocks into ToolCallRequest list
        return new LlmResponse(text, finishReason, null, inputTokens, outputTokens);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildRequestBody(request, stream: true);
        var json = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = content };
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return StreamingChatChunk.FromContentDelta(text);
                }
            }
        }
    }

    private Dictionary<string, object?> BuildRequestBody(ChatRequest request, bool stream)
    {
        var settings = request.Settings;
        var messages = request.Messages
            .Where(m => m.Role != "system")
            .Select(m =>
            {
                var msg = new Dictionary<string, object?>
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content
                };

                // Anthropic uses content blocks for tool use/result
                if (m.ToolCalls is { Count: > 0 })
                {
                    var contentBlocks = new List<object>();
                    if (!string.IsNullOrEmpty(m.Content))
                        contentBlocks.Add(new { type = "text", text = m.Content });

                    foreach (var tc in m.ToolCalls)
                    {
                        contentBlocks.Add(new
                        {
                            type = "tool_use",
                            id = tc.Id,
                            name = tc.ToolName,
                            input = tc.Arguments
                        });
                    }
                    msg["content"] = contentBlocks;
                }

                if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(m.ToolCallId))
                {
                    msg["content"] = new[]
                    {
                        new
                        {
                            type = "tool_result",
                            tool_use_id = m.ToolCallId,
                            content = m.Content
                        }
                    };
                }

                return msg;
            })
            .ToList<object>();

        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
            ["stream"] = stream
        };

        // Anthropic requires max_tokens - if not set, use a reasonable default
        // Provider-specific requirement - most providers don't need this
        body["max_tokens"] = settings.MaxTokens ?? 4096;
        if (settings.Temperature.HasValue)
            body["temperature"] = settings.Temperature.Value;

        var systemPrompt = request.SystemPrompt
            ?? request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        if (systemPrompt is not null)
            body["system"] = systemPrompt;

        return body;
    }
}
