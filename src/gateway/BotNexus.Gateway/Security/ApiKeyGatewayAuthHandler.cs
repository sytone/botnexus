using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Security;

/// <summary>
/// API key authentication handler for Gateway HTTP requests.
/// </summary>
/// <remarks>
/// <para>
/// If no API key is configured, authentication runs in development mode and allows all requests.
/// </para>
/// <para>
/// If keys are configured, callers must provide either <c>Authorization: Bearer {key}</c>
/// or <c>X-Api-Key: {key}</c>.
/// </para>
/// <para>
/// Step 3/5 of the security-event taxonomy (#1646, part of #1526): every handshake outcome
/// emits one <see cref="SecurityEvent"/> with <see cref="SecurityEventCategory.Auth"/> to a
/// trusted <see cref="ISecurityEventSink"/> - accepted is success, rejected is failure.
/// Emission is best-effort and never participates in the decision: a null sink is a no-op and
/// a sink fault is swallowed/logged so authentication can never be broken by observability.
/// These events go only to the trusted sink, never to the public diagnostic stream.
/// </para>
/// </remarks>
public sealed class ApiKeyGatewayAuthHandler : IGatewayAuthHandler
{
    private const string AuthorizationHeader = "Authorization";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string BearerPrefix = "Bearer ";

    /// <summary>The target reference reported on every auth handshake event.</summary>
    private const string GatewayTarget = "gateway";

    private readonly Lock _sync = new();
    private IReadOnlyDictionary<string, GatewayCallerIdentity> _identitiesByApiKey;
    private readonly IOptionsMonitor<PlatformConfig>? _platformConfig;
    private readonly ILogger<ApiKeyGatewayAuthHandler> _logger;
    private readonly ISecurityEventSink? _securityEvents;

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
    /// <param name="securityEvents">Trusted security-event sink, or null to disable emission.</param>
    public ApiKeyGatewayAuthHandler(
        string? apiKey,
        ILogger<ApiKeyGatewayAuthHandler> logger,
        ISecurityEventSink? securityEvents = null)
    {
        _logger = logger;
        _platformConfig = null;
        _securityEvents = securityEvents;
        _identitiesByApiKey = BuildIdentityMap(apiKey, apiKeys: null);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyGatewayAuthHandler"/> class.
    /// </summary>
    /// <param name="platformConfig">Platform config.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="securityEvents">Trusted security-event sink, or null to disable emission.</param>
    public ApiKeyGatewayAuthHandler(
        PlatformConfig platformConfig,
        ILogger<ApiKeyGatewayAuthHandler> logger,
        ISecurityEventSink? securityEvents = null)
    {
        ArgumentNullException.ThrowIfNull(platformConfig);
        _logger = logger;
        _platformConfig = null;
        _securityEvents = securityEvents;
        _identitiesByApiKey = BuildIdentityMap(platformConfig.ApiKey, platformConfig.Gateway?.ApiKeys, platformConfig.Gateway?.Satellites);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyGatewayAuthHandler"/> class.
    /// </summary>
    /// <param name="platformConfig">Platform config monitor.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="securityEvents">Trusted security-event sink, or null to disable emission.</param>
    public ApiKeyGatewayAuthHandler(
        IOptionsMonitor<PlatformConfig> platformConfig,
        ILogger<ApiKeyGatewayAuthHandler> logger,
        ISecurityEventSink? securityEvents = null)
    {
        ArgumentNullException.ThrowIfNull(platformConfig);
        _logger = logger;
        _platformConfig = platformConfig;
        _securityEvents = securityEvents;
        _identitiesByApiKey = BuildIdentityMap(
            platformConfig.CurrentValue.ApiKey,
            platformConfig.CurrentValue.Gateway?.ApiKeys);
    }

    /// <inheritdoc />
    public string Scheme => "ApiKey";

    /// <inheritdoc />
    public Task<GatewayAuthResult> AuthenticateAsync(
        GatewayAuthContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identitiesByApiKey = GetIdentityMap();

        if (identitiesByApiKey.Count == 0)
        {
            _logger.LogDebug("Gateway auth is running in development mode: no API key configured.");
            EmitOutcome(success: true, actorId: "development");
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
        {
            EmitOutcome(success: false, actorId: "anonymous");
            return Task.FromResult(GatewayAuthResult.Failure("Missing API key. Provide X-Api-Key or Authorization: Bearer <key>."));
        }

        if (!identitiesByApiKey.TryGetValue(presentedKey, out var identity))
        {
            EmitOutcome(success: false, actorId: presentedKey);
            return Task.FromResult(GatewayAuthResult.Failure("Invalid API key."));
        }

        EmitOutcome(success: true, actorId: identity.CallerId);
        return Task.FromResult(GatewayAuthResult.Success(identity));
    }

    private static Dictionary<string, GatewayCallerIdentity> BuildIdentityMap(
        string? legacyApiKey,
        Dictionary<string, ApiKeyConfig>? apiKeys,
        Dictionary<string, SatelliteConfig>? satellites = null)
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

        if (apiKeys is not null)
        {
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
        }

        if (satellites is not null)
        {
            foreach (var (satId, satConfig) in satellites)
            {
                if (!satConfig.Enabled || string.IsNullOrWhiteSpace(satConfig.ApiKey))
                    continue;

                map[satConfig.ApiKey] = new GatewayCallerIdentity
                {
                    CallerId = $"satellite:{satId}",
                    DisplayName = satConfig.DisplayName ?? $"Satellite {satId}",
                    TenantId = "default",
                    AllowedAgents = [],
                    Permissions = ["satellite:connect", "satellite:heartbeat"],
                    IsAdmin = false
                };
            }
        }

        return map;
    }

    private IReadOnlyDictionary<string, GatewayCallerIdentity> GetIdentityMap()
    {
        if (_platformConfig is null)
            return _identitiesByApiKey;

        var currentConfig = _platformConfig.CurrentValue;
        var rebuilt = BuildIdentityMap(
            currentConfig.ApiKey,
            currentConfig.Gateway?.ApiKeys,
            currentConfig.Gateway?.Satellites);

        lock (_sync)
        {
            _identitiesByApiKey = rebuilt;
            return _identitiesByApiKey;
        }
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

    /// <summary>
    /// Emits one auth-boundary security event to the trusted sink. The actor id is a salted hash
    /// of the caller/presented credential so the trusted record never carries the raw secret or
    /// identity. Best-effort: a null sink is a no-op and any sink fault is swallowed/logged so the
    /// authentication decision is never blocked or altered.
    /// </summary>
    private void EmitOutcome(bool success, string actorId)
    {
        if (_securityEvents is null)
            return;

        try
        {
            var evt = SecurityEvent.AuthOutcome(
                success ? "gateway.auth.accepted" : "gateway.auth.rejected",
                success,
                actor: new SecurityEventActor(SecurityActorKind.Node, HashActor(actorId)))
            with
            {
                Target = new SecurityEventTarget(SecurityTargetKind.Gateway, GatewayTarget)
            };
            _securityEvents.Record(evt);
        }
        catch (Exception ex)
        {
            // Observability must never break the auth path; swallow and log.
            _logger.LogWarning(ex, "Failed to record gateway auth security event (success={Success}).", success);
        }
    }

    /// <summary>
    /// Hashes a caller id or presented credential to a short, opaque hex token so security events
    /// carry a stable pseudonym instead of the raw value. SHA-256 truncated to 8 bytes is enough
    /// for correlation; it is not reversible and never stores the plaintext.
    /// </summary>
    private static string HashActor(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id ?? string.Empty));
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
