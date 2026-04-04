using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Copilot;

/// <summary>
/// GitHub Copilot provider using the new architecture.
/// Routes requests to appropriate API format handlers based on model.
/// </summary>
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
    
    private readonly Dictionary<string, IApiFormatHandler> _handlers;

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
        if (config.TimeoutSeconds > 0)
            _httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
        
        // Initialize API format handlers
        _handlers = new Dictionary<string, IApiFormatHandler>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-messages"] = new AnthropicMessagesHandler(_httpClient, Logger),
            ["openai-completions"] = new OpenAiCompletionsHandler(_httpClient, Logger),
            ["openai-responses"] = new OpenAiResponsesHandler(_httpClient, Logger)
        };
    }

    public override string DefaultModel => _defaultModel;

    public bool HasValidToken => _cachedToken is { IsExpired: false };

    public override Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        // Return all registered Copilot models from the registry
        var modelIds = CopilotModels.All.Select(m => m.Id).ToArray();
        Logger.LogDebug("Returning {Count} registered Copilot models", modelIds.Length);
        return Task.FromResult<IReadOnlyList<string>>(modelIds);
    }

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
                Logger.LogWarning("OAuth token for Copilot has expired (expired at {ExpiresAt:u}). Clearing and prompting for re-authentication.", _cachedToken.ExpiresAt);
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

        // Ensure we have a valid GitHub OAuth token before attempting exchange
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
            Logger.LogWarning("Copilot token exchange failed (HTTP {StatusCode}): {Body}. Clearing cached GitHub OAuth token and initiating re-authentication.",
                (int)exchangeResponse.StatusCode, body);
            await InvalidateAndClearTokensAsync(cancellationToken).ConfigureAwait(false);
            
            // Retry authentication once - this will trigger device auth flow
            Logger.LogInformation("Initiating device authentication flow...");
            githubToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            
            // Retry exchange with fresh token
            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, CopilotConfig.CopilotTokenExchangeUrl);
            retryRequest.Headers.TryAddWithoutValidation("Authorization", $"token {githubToken}");
            retryRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
            retryRequest.Headers.TryAddWithoutValidation("User-Agent", CopilotConfig.UserAgentValue);
            retryRequest.Headers.TryAddWithoutValidation("Editor-Version", CopilotConfig.EditorVersion);
            retryRequest.Headers.TryAddWithoutValidation("Editor-Plugin-Version", CopilotConfig.EditorPluginVersion);
            
            exchangeResponse = await _httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Routes the request to the appropriate API format handler based on the model.
    /// </summary>
    protected override async Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var modelId = string.IsNullOrWhiteSpace(request.Settings.Model) ? _defaultModel : request.Settings.Model;
        
        if (!CopilotModels.TryResolve(modelId, out var model))
        {
            Logger.LogWarning("Model {ModelId} not found in registry, falling back to default {DefaultModel}", 
                modelId, _defaultModel);
            model = CopilotModels.Resolve(_defaultModel);
        }
        
        if (model is null)
            throw new InvalidOperationException($"Failed to resolve model: {modelId}");
        
        var handler = GetHandler(model.Api);
        var apiKey = await GetCopilotAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        
        Logger.LogInformation("Routing to {ApiFormat} handler for model {ModelId}", model.Api, model.Id);
        
        try
        {
            return await handler.ChatAsync(model, request, apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
        {
            Logger.LogWarning("Authentication failed, clearing cached tokens and triggering re-authentication");
            await InvalidateAndClearTokensAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Authentication required. Token expired or missing. Re-authentication will be triggered on next request.", ex);
        }
    }

    public override async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var modelId = string.IsNullOrWhiteSpace(request.Settings.Model) ? _defaultModel : request.Settings.Model;
        
        if (!CopilotModels.TryResolve(modelId, out var model))
        {
            Logger.LogWarning("Model {ModelId} not found in registry, falling back to default {DefaultModel}", 
                modelId, _defaultModel);
            model = CopilotModels.Resolve(_defaultModel);
        }
        
        if (model is null)
            throw new InvalidOperationException($"Failed to resolve model: {modelId}");
        
        var handler = GetHandler(model.Api);
        var apiKey = await GetCopilotAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        
        Logger.LogInformation("Routing to {ApiFormat} streaming handler for model {ModelId}", model.Api, model.Id);
        
        await foreach (var chunk in handler.ChatStreamAsync(model, request, apiKey, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    
    private IApiFormatHandler GetHandler(string apiFormat)
    {
        if (_handlers.TryGetValue(apiFormat, out var handler))
            return handler;
        
        throw new ArgumentException($"Unknown API format: {apiFormat}. Available: {string.Join(", ", _handlers.Keys)}", nameof(apiFormat));
    }

    private async Task InvalidateAndClearTokensAsync(CancellationToken cancellationToken)
    {
        _copilotAccessToken = null;
        _copilotExpiresAt = default;
        _cachedToken = null;
        await _tokenStore.ClearTokenAsync("copilot", cancellationToken).ConfigureAwait(false);
    }
}
