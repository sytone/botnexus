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
    private string? _copilotAccessToken;
    private DateTimeOffset _copilotExpiresAt;
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(CopilotConfig.UserAgentValue);
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

    private async Task<string> GetCopilotAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_copilotAccessToken is not null && DateTimeOffset.UtcNow < _copilotExpiresAt - ExpirySkew)
            return _copilotAccessToken;

        var githubToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        using var exchangeRequest = new HttpRequestMessage(HttpMethod.Get, CopilotConfig.CopilotTokenExchangeUrl);
        exchangeRequest.Headers.TryAddWithoutValidation("Authorization", $"token {githubToken}");
        exchangeRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
        exchangeRequest.Headers.TryAddWithoutValidation("User-Agent", CopilotConfig.UserAgentValue);
        exchangeRequest.Headers.TryAddWithoutValidation("Editor-Version", CopilotConfig.EditorVersion);
        exchangeRequest.Headers.TryAddWithoutValidation("Editor-Plugin-Version", CopilotConfig.EditorPluginVersion);

        var exchangeResponse = await _httpClient.SendAsync(exchangeRequest, cancellationToken).ConfigureAwait(false);

        if (exchangeResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            var body = await exchangeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Logger.LogWarning("Copilot token exchange failed (HTTP {StatusCode}): {Body}",
                (int)exchangeResponse.StatusCode, body);
            _cachedToken = null;
            await _tokenStore.ClearTokenAsync("copilot", cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Copilot token exchange failed: {exchangeResponse.StatusCode}");
        }

        exchangeResponse.EnsureSuccessStatusCode();

        var json = await exchangeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var token = root.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Copilot token exchange returned no token.");

        if (root.TryGetProperty("expires_at", out var expiresAtElement))
        {
            _copilotExpiresAt = expiresAtElement.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAtElement.GetInt64())
                : DateTimeOffset.UtcNow.AddSeconds(1500);
        }
        else
        {
            var refreshIn = root.TryGetProperty("refresh_in", out var refreshInElement)
                ? refreshInElement.GetInt32()
                : 1500;
            _copilotExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshIn);
        }

        _copilotAccessToken = token;
        Logger.LogDebug("Copilot token exchanged, expires at {ExpiresAt}", _copilotExpiresAt);
        return _copilotAccessToken;
    }

    protected override async Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var payload = BuildRequestPayload(request, stream: false);
        using var httpRequest = await CreateChatRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Logger.LogWarning("Copilot API returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _copilotAccessToken = null;
                _copilotExpiresAt = default;
            }
        }

        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(responseContent);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var content = message.TryGetProperty("content", out var contentElement)
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;
        var finishReasonStr = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            ? finishReasonElement.GetString()
            : null;
        var finishReason = MapFinishReason(finishReasonStr);

        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        if (message.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
            toolCalls = ParseToolCalls(toolCallsElement);

        Logger.LogDebug("Copilot response: content_length={ContentLength}, finish_reason={FinishReasonRaw}/{FinishReasonMapped}, tool_calls={ToolCallCount}",
            content?.Length ?? 0, finishReasonStr ?? "null", finishReason, toolCalls?.Count ?? 0);

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

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Logger.LogWarning("Copilot API returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _copilotAccessToken = null;
                _copilotExpiresAt = default;
            }
        }

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

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

    private async Task InvalidateTokenAsync(CancellationToken cancellationToken)
    {
        _copilotAccessToken = null;
        _copilotExpiresAt = default;
    }

    private async Task<HttpRequestMessage> CreateChatRequestAsync(
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var accessToken = await GetCopilotAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Editor-Version", CopilotConfig.EditorVersion);
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", CopilotConfig.EditorPluginVersion);
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
            ["model"] = string.IsNullOrWhiteSpace(request.Settings.Model) ? _defaultModel : request.Settings.Model,
            ["messages"] = messages,
            ["stream"] = stream
        };

        // Only include max_tokens and temperature if explicitly set
        if (request.Settings.MaxTokens.HasValue)
            payload["max_tokens"] = request.Settings.MaxTokens.Value;
        if (request.Settings.Temperature.HasValue)
            payload["temperature"] = request.Settings.Temperature.Value;

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
