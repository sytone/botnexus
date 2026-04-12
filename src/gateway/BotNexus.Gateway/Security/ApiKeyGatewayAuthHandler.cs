using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Security;

/// <summary>
/// API key authentication handler for Gateway HTTP and WebSocket requests.
/// </summary>
/// <remarks>
/// <para>
/// If no API key is configured, authentication runs in development mode and allows all requests.
/// </para>
/// <para>
/// If keys are configured, callers must provide either <c>Authorization: Bearer {key}</c>
/// or <c>X-Api-Key: {key}</c>.
/// </para>
/// </remarks>
public sealed class ApiKeyGatewayAuthHandler : IGatewayAuthHandler
{
    private const string AuthorizationHeader = "Authorization";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string BearerPrefix = "Bearer ";

    private readonly IReadOnlyDictionary<string, GatewayCallerIdentity> _identitiesByApiKey;
    private readonly ILogger<ApiKeyGatewayAuthHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyGatewayAuthHandler"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public ApiKeyGatewayAuthHandler(ILogger<ApiKeyGatewayAuthHandler> logger)
        : this(apiKey: null, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyGatewayAuthHandler"/> class.
    /// </summary>
    /// <param name="apiKey">Configured legacy API key. Null or empty enables development mode.</param>
    /// <param name="logger">Logger instance.</param>
    public ApiKeyGatewayAuthHandler(string? apiKey, ILogger<ApiKeyGatewayAuthHandler> logger)
    {
        _logger = logger;
        _identitiesByApiKey = BuildIdentityMap(apiKey, apiKeys: null);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyGatewayAuthHandler"/> class.
    /// </summary>
    /// <param name="platformConfig">Platform config.</param>
    /// <param name="logger">Logger instance.</param>
    public ApiKeyGatewayAuthHandler(PlatformConfig platformConfig, ILogger<ApiKeyGatewayAuthHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(platformConfig);
        _logger = logger;
        _identitiesByApiKey = BuildIdentityMap(platformConfig.ApiKey, platformConfig.Gateway?.ApiKeys);
    }

    /// <inheritdoc />
    public string Scheme => "ApiKey";

    /// <inheritdoc />
    public Task<GatewayAuthResult> AuthenticateAsync(
        GatewayAuthContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_identitiesByApiKey.Count == 0)
        {
            _logger.LogDebug("Gateway auth is running in development mode: no API key configured.");
            return Task.FromResult(GatewayAuthResult.Success(new GatewayCallerIdentity
            {
                CallerId = "gateway-dev",
                DisplayName = "Gateway Development Caller",
                TenantId = "development",
                Permissions = ["*"],
                IsAdmin = true
            }));
        }

        var presentedKey = ExtractApiKey(context.Headers);
        if (presentedKey is null)
            return Task.FromResult(GatewayAuthResult.Failure("Missing API key. Provide X-Api-Key or Authorization: Bearer <key>."));

        if (!_identitiesByApiKey.TryGetValue(presentedKey, out var identity))
            return Task.FromResult(GatewayAuthResult.Failure("Invalid API key."));

        return Task.FromResult(GatewayAuthResult.Success(identity));
    }

    private static Dictionary<string, GatewayCallerIdentity> BuildIdentityMap(
        string? legacyApiKey,
        Dictionary<string, ApiKeyConfig>? apiKeys)
    {
        var map = new Dictionary<string, GatewayCallerIdentity>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(legacyApiKey))
        {
            map[legacyApiKey] = new GatewayCallerIdentity
            {
                CallerId = "gateway-api-key",
                DisplayName = "Gateway API Key Caller",
                TenantId = "default",
                Permissions = ["*"],
                IsAdmin = true
            };
        }

        if (apiKeys is null)
            return map;

        foreach (var (keyId, keyConfig) in apiKeys)
        {
            if (string.IsNullOrWhiteSpace(keyConfig.ApiKey))
                continue;

            var callerId = !string.IsNullOrWhiteSpace(keyConfig.CallerId)
                ? keyConfig.CallerId
                : $"gateway-key:{keyId}";
            var tenantId = !string.IsNullOrWhiteSpace(keyConfig.TenantId)
                ? keyConfig.TenantId
                : "default";

            map[keyConfig.ApiKey] = new GatewayCallerIdentity
            {
                CallerId = callerId,
                DisplayName = keyConfig.DisplayName,
                TenantId = tenantId,
                AllowedAgents = keyConfig.AllowedAgents ?? [],
                Permissions = keyConfig.Permissions ?? [],
                IsAdmin = keyConfig.IsAdmin
            };
        }

        return map;
    }

    private static string? ExtractApiKey(IReadOnlyDictionary<string, string> headers)
    {
        if (TryGetHeaderValue(headers, ApiKeyHeader, out var apiKeyHeaderValue) &&
            !string.IsNullOrWhiteSpace(apiKeyHeaderValue))
        {
            return apiKeyHeaderValue.Trim();
        }

        if (!TryGetHeaderValue(headers, AuthorizationHeader, out var authorizationValue))
            return null;

        if (string.IsNullOrWhiteSpace(authorizationValue) ||
            !authorizationValue.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationValue[BearerPrefix.Length..].Trim();
        return token.Length > 0 ? token : null;
    }

    private static bool TryGetHeaderValue(
        IReadOnlyDictionary<string, string> headers,
        string headerName,
        out string value)
    {
        if (headers.TryGetValue(headerName, out value!))
            return true;

        foreach (var (key, candidateValue) in headers)
        {
            if (string.Equals(key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
