using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Copilot;

public sealed class CopilotProvider : LlmProviderBase, IOAuthProvider
{
    private readonly HttpClient _httpClient;
    private readonly CopilotConfig _config;
    private readonly IOAuthTokenStore _tokenStore;
    private readonly GitHubDeviceCodeFlow _deviceCodeFlow;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private OAuthToken? _cachedToken;
    private readonly string _defaultModel;

    public CopilotProvider(
        CopilotConfig config,
        IOAuthTokenStore tokenStore,
        GitHubDeviceCodeFlow deviceCodeFlow,
        ILogger<CopilotProvider>? logger = null,
        HttpClient? httpClient = null)
        : base(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotProvider>.Instance, config.MaxRetries)
    {
        _config = config;
        _tokenStore = tokenStore;
        _deviceCodeFlow = deviceCodeFlow;
        _defaultModel = string.IsNullOrWhiteSpace(config.DefaultModel)
            ? CopilotConfig.DefaultModelName
            : config.DefaultModel;
        Generation = new GenerationSettings { Model = _defaultModel };

        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(config.ApiBase ?? CopilotConfig.DefaultApiBaseUrl);
        if (config.TimeoutSeconds > 0)
            _httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public override string DefaultModel => _defaultModel;

    public bool HasValidToken => _cachedToken is { IsExpired: false };

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (HasValidToken)
            return _cachedToken!.AccessToken;

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasValidToken)
                return _cachedToken!.AccessToken;

            _cachedToken ??= await _tokenStore.LoadTokenAsync("copilot", cancellationToken).ConfigureAwait(false);
            if (_cachedToken is { IsExpired: false })
                return _cachedToken.AccessToken;

            if (_cachedToken is { IsExpired: true })
            {
                await _tokenStore.ClearTokenAsync("copilot", cancellationToken).ConfigureAwait(false);
                _cachedToken = null;
            }

            var token = await _deviceCodeFlow
                .AcquireAccessTokenAsync(_config.OAuthClientId, cancellationToken)
                .ConfigureAwait(false);

            await _tokenStore.SaveTokenAsync("copilot", token, cancellationToken).ConfigureAwait(false);
            _cachedToken = token;
            return token.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    protected override async Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var payload = BuildRequestPayload(request, stream: false);
        using var httpRequest = await CreateChatRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(responseContent);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var content = message.TryGetProperty("content", out var contentElement)
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            ? MapFinishReason(finishReasonElement.GetString())
            : FinishReason.Other;

        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        if (message.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
            toolCalls = ParseToolCalls(toolCallsElement);

        int? promptTokens = null;
        int? completionTokens = null;
        if (root.TryGetProperty("usage", out var usageElement))
        {
            if (usageElement.TryGetProperty("prompt_tokens", out var promptTokensElement))
                promptTokens = promptTokensElement.GetInt32();
            if (usageElement.TryGetProperty("completion_tokens", out var completionTokensElement))
                completionTokens = completionTokensElement.GetInt32();
        }

        return new LlmResponse(content, finishReason, toolCalls, promptTokens, completionTokens);
    }

    public override async IAsyncEnumerable<string> ChatStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildRequestPayload(request, stream: true);
        using var httpRequest = await CreateChatRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
            catch
            {
                continue;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var delta = choices[0].TryGetProperty("delta", out var deltaElement)
                    ? deltaElement
                    : default;

                if (delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var contentElement) &&
                    contentElement.ValueKind == JsonValueKind.String)
                {
                    var content = contentElement.GetString();
                    if (!string.IsNullOrEmpty(content))
                        yield return content;
                }
            }
        }
    }

    private async Task<HttpRequestMessage> CreateChatRequestAsync(
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private Dictionary<string, object?> BuildRequestPayload(ChatRequest request, bool stream)
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
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(request.Settings.Model) ? _defaultModel : request.Settings.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.Settings.MaxTokens,
            ["temperature"] = request.Settings.Temperature,
            ["stream"] = stream
        };

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

    private static FinishReason MapFinishReason(string? reason) => reason switch
    {
        "stop" => FinishReason.Stop,
        "tool_calls" => FinishReason.ToolCalls,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Other
    };

    private static IReadOnlyList<ToolCallRequest> ParseToolCalls(JsonElement toolCallsElement)
    {
        var result = new List<ToolCallRequest>();

        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            var id = toolCall.TryGetProperty("id", out var idElement)
                ? idElement.GetString() ?? string.Empty
                : string.Empty;

            if (!toolCall.TryGetProperty("function", out var functionElement))
                continue;

            var name = functionElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            var argumentsJson = functionElement.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.GetString()
                : null;

            var arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];

            result.Add(new ToolCallRequest(id, name, arguments));
        }

        return result;
    }
}
