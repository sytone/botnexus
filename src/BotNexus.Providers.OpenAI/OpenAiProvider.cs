using System.ClientModel;
using System.Runtime.CompilerServices;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAiChatMessage = OpenAI.Chat.ChatMessage;
using BotNexusChatMessage = BotNexus.Core.Models.ChatMessage;

namespace BotNexus.Providers.OpenAI;

/// <summary>
/// OpenAI-compatible LLM provider using the official OpenAI .NET SDK.
/// Supports any OpenAI-compatible endpoint (OpenAI, Azure OpenAI, local models).
/// </summary>
public sealed class OpenAiProvider : LlmProviderBase
{
    private readonly ChatClient _chatClient;
    private readonly OpenAIClient _openAiClient;
    private readonly string _defaultModel;
    private readonly string? _apiBase;
    private readonly string _apiKey;

    public OpenAiProvider(
        string apiKey,
        string model = "gpt-4o",
        string? apiBase = null,
        ILogger<OpenAiProvider>? logger = null,
        int maxRetries = 3)
        : base(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance, maxRetries)
    {
        _apiKey = apiKey;
        _defaultModel = model;
        _apiBase = apiBase;
        Generation = new GenerationSettings { Model = model };

        OpenAIClientOptions? options = null;
        if (apiBase is not null)
        {
            options = new OpenAIClientOptions { Endpoint = new Uri(apiBase) };
        }

        _openAiClient = options is not null
            ? new OpenAIClient(new ApiKeyCredential(apiKey), options)
            : new OpenAIClient(new ApiKeyCredential(apiKey));

        _chatClient = _openAiClient.GetChatClient(model);
    }

    /// <inheritdoc/>
    public override string DefaultModel => _defaultModel;

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpClient = new HttpClient();
            var baseUrl = _apiBase ?? "https://api.openai.com";
            httpClient.BaseAddress = new Uri(baseUrl);
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await httpClient.GetAsync("/v1/models", cancellationToken).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("Failed to fetch OpenAI models: HTTP {StatusCode}", (int)response.StatusCode);
                return new[] { _defaultModel };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var models = new List<string>();
                foreach (var model in dataElement.EnumerateArray())
                {
                    if (model.TryGetProperty("id", out var idElement))
                    {
                        var id = idElement.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                            models.Add(id);
                    }
                }

                if (models.Count > 0)
                {
                    Logger.LogDebug("Fetched {Count} models from OpenAI API", models.Count);
                    return models;
                }
            }

            Logger.LogWarning("No models found in OpenAI API response, falling back to default");
            return new[] { _defaultModel };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch available models from OpenAI, falling back to default");
            return new[] { _defaultModel };
        }
    }

    /// <summary>
    /// Normalizes OpenAI SDK response to canonical LlmResponse format.
    /// 
    /// <para><b>Provider-Specific Normalization Responsibilities:</b></para>
    /// <list type="bullet">
    ///   <item><b>Single content block:</b> OpenAI SDK returns Content as array, we take first item's Text.</item>
    ///   
    ///   <item><b>Tool argument format:</b> OpenAI always returns arguments as JSON string (standard format), 
    ///   which we deserialize to Dictionary&lt;string, object?&gt;.</item>
    ///   
    ///   <item><b>Finish reason mapping:</b> OpenAI SDK uses ChatFinishReason enum which we map to our 
    ///   canonical FinishReason enum.</item>
    ///   
    ///   <item><b>Token count normalization:</b> OpenAI uses "InputTokenCount" and "OutputTokenCount" 
    ///   which map directly to our InputTokens and OutputTokens.</item>
    /// </list>
    /// 
    /// <para>
    /// The OpenAI provider benefits from the official SDK which abstracts HTTP and provides strongly-typed
    /// response objects. This makes normalization straightforward compared to raw JSON parsing providers.
    /// </para>
    /// </summary>
    protected override async Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var actualModel = string.IsNullOrWhiteSpace(request.Settings.Model) ? _defaultModel : request.Settings.Model;
        Logger.LogDebug("OpenAiProvider: Sending chat request with model={Model}", actualModel);
        var messages = BuildMessages(request);
        var options = BuildChatCompletionOptions(request);

        var completion = await _chatClient.CompleteChatAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var result = completion.Value;

        // NORMALIZATION: Extract first content block's text
        var content = result.Content.Count > 0 ? result.Content[0].Text : string.Empty;
        
        // NORMALIZATION: Map SDK enum to canonical enum
        var finishReason = MapFinishReason(result.FinishReason);
        
        // NORMALIZATION: Parse tool calls (handles JSON string arguments)
        var toolCalls = MapToolCalls(result.ToolCalls);

        return new LlmResponse(
            content,
            finishReason,
            toolCalls,
            result.Usage?.InputTokenCount,
            result.Usage?.OutputTokenCount);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(request);
        var options = BuildChatCompletionOptions(request);

        await foreach (var update in _chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return StreamingChatChunk.FromContentDelta(part.Text);
            }
        }
    }

    private List<OpenAiChatMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<OpenAiChatMessage>();

        if (request.SystemPrompt is not null)
            messages.Add(new SystemChatMessage(request.SystemPrompt));

        foreach (var msg in request.Messages)
        {
            messages.Add(msg.Role switch
            {
                "system" => new SystemChatMessage(msg.Content),
                "assistant" => new AssistantChatMessage(msg.Content),
                "tool" => new ToolChatMessage(msg.Content, msg.Content),
                _ => (OpenAiChatMessage)new UserChatMessage(msg.Content)
            });
        }

        return messages;
    }

    private ChatCompletionOptions BuildChatCompletionOptions(ChatRequest request)
    {
        var settings = request.Settings;
        var options = new ChatCompletionOptions();

        // Only set max tokens and temperature if explicitly configured
        if (settings.MaxTokens.HasValue)
            options.MaxOutputTokenCount = settings.MaxTokens.Value;
        if (settings.Temperature.HasValue)
            options.Temperature = (float)settings.Temperature.Value;

        if (request.Tools is { Count: > 0 })
        {
            foreach (var tool in request.Tools)
            {
                var toolDef = ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    BuildParameterSchema(tool));
                options.Tools.Add(toolDef);
            }
        }

        return options;
    }

    private static BinaryData BuildParameterSchema(Core.Models.ToolDefinition tool)
    {
        var required = tool.Parameters
            .Where(p => p.Value.Required)
            .Select(p => p.Key)
            .ToList();

        var properties = tool.Parameters.ToDictionary(
            p => p.Key,
            p =>
            {
                var schema = new Dictionary<string, object>
                {
                    ["type"] = p.Value.Type,
                    ["description"] = p.Value.Description
                };
                if (p.Value.EnumValues is { Count: > 0 })
                    schema["enum"] = p.Value.EnumValues;
                if (p.Value.Items is not null)
                {
                    schema["items"] = new Dictionary<string, object>
                    {
                        ["type"] = p.Value.Items.Type,
                        ["description"] = p.Value.Items.Description
                    };
                }
                return (object)schema;
            });

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };

        return BinaryData.FromObjectAsJson(schema);
    }

    /// <summary>
    /// Maps OpenAI SDK finish reason enum to canonical FinishReason enum.
    /// Part of the provider normalization layer.
    /// </summary>
    private static FinishReason MapFinishReason(ChatFinishReason? reason) => reason switch
    {
        ChatFinishReason.Stop => FinishReason.Stop,
        ChatFinishReason.ToolCalls => FinishReason.ToolCalls,
        ChatFinishReason.Length => FinishReason.Length,
        ChatFinishReason.ContentFilter => FinishReason.ContentFilter,
        _ => FinishReason.Other
    };

    /// <summary>
    /// Normalizes OpenAI SDK tool calls to canonical ToolCallRequest list.
    /// 
    /// <para>
    /// OpenAI always returns tool arguments as a JSON string (via FunctionArguments.ToString()).
    /// We deserialize this to Dictionary&lt;string, object?&gt; for the canonical format.
    /// Malformed JSON is ignored gracefully (empty dict) rather than failing the entire response.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ToolCallRequest>? MapToolCalls(IReadOnlyList<ChatToolCall> toolCalls)
    {
        if (toolCalls is not { Count: > 0 }) return null;

        return toolCalls.Select(tc =>
        {
            Dictionary<string, object?> args = [];
            if (tc.FunctionArguments.ToString() is { } json && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    // OpenAI format: arguments is always a JSON string
                    args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                        ?? [];
                }
                catch { /* ignore malformed tool args */ }
            }
            return new ToolCallRequest(tc.Id, tc.FunctionName, args);
        }).ToList();
    }
}
