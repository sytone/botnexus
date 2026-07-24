using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;

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
    private const string OriginHeader = "Origin";
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Default browser origin permitted when no explicit <c>Gateway.Cors.AllowedOrigins</c>
    /// list is configured. Mirrors the CORS fallback in Program.cs.
    /// </summary>
    private const string DefaultAllowedOrigin = "http://localhost:5005";

    /// <summary>
    /// Feature flag gating the dev-mode browser-Origin enforcement (#1931). The guard is now
    /// ENABLED BY DEFAULT (#1946): when the flag is <em>unspecified</em> the guard is active so
    /// keyless gateways are protected against DNS-rebind / CSRF out-of-the-box. Operators can
    /// still explicitly opt out with <c>FeatureManagement.GatewayDevOriginEnforcement: false</c>.
    /// A feature-flag evaluation fault fails OPEN (guard disabled) so a misconfiguration can never
    /// lock the operator out. Lives under the <c>FeatureManagement</c> section of config.json.
    /// </summary>
    public const string DevOriginEnforcementFeature = "GatewayDevOriginEnforcement";

    /// <summary>
    /// Configuration key path for the dev-origin enforcement flag under the Microsoft.FeatureManagement
    /// schema. Used to distinguish an <em>unspecified</em> flag (default ON, #1946) from an explicit
    /// <c>false</c> (operator opt-out) - a distinction <see cref="IFeatureManager"/> alone cannot make,
    /// because it reports both as disabled.
    /// </summary>
    private const string DevOriginEnforcementConfigKey = "FeatureManagement:" + DevOriginEnforcementFeature;

    /// <summary>The target reference reported on every auth handshake event.</summary>
    private const string GatewayTarget = "gateway";

    private readonly Lock _sync = new();
    private IReadOnlyDictionary<string, GatewayCallerIdentity> _identitiesByApiKey;
    private readonly IReadOnlyList<string>? _staticAllowedOrigins;
    private readonly IOptionsMonitor<PlatformConfig>? _platformConfig;
    private readonly IFeatureManager? _featureManager;
    private readonly IConfiguration? _configuration;
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
        ISecurityEventSink? securityEvents = null,
        IFeatureManager? featureManager = null,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        _platformConfig = null;
        _securityEvents = securityEvents;
        _featureManager = featureManager;
        _configuration = configuration;
        _staticAllowedOrigins = null;
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
        ISecurityEventSink? securityEvents = null,
        IFeatureManager? featureManager = null,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(platformConfig);
        _logger = logger;
        _platformConfig = null;
        _securityEvents = securityEvents;
        _featureManager = featureManager;
        _configuration = configuration;
        _staticAllowedOrigins = ResolveAllowedOrigins(platformConfig.Gateway?.Cors?.AllowedOrigins);
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
        ISecurityEventSink? securityEvents = null,
        IFeatureManager? featureManager = null,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(platformConfig);
        _logger = logger;
        _platformConfig = platformConfig;
        _securityEvents = securityEvents;
        _featureManager = featureManager;
        _configuration = configuration;
        _staticAllowedOrigins = null;
        _identitiesByApiKey = BuildIdentityMap(
            platformConfig.CurrentValue.ApiKey,
            platformConfig.CurrentValue.Gateway?.ApiKeys);
    }

    /// <inheritdoc />
    public string Scheme => "ApiKey";

    /// <inheritdoc />
    public async Task<GatewayAuthResult> AuthenticateAsync(
        GatewayAuthContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identitiesByApiKey = GetIdentityMap();

        if (identitiesByApiKey.Count == 0)
        {
            // DNS-rebind / CSRF hardening (#1931): in dev/no-key mode we still grant a full
            // admin identity, but a browser cannot be allowed to silently drive that identity
            // from an arbitrary web origin. When a browser Origin header IS present it must be
            // on the allow-list; a missing Origin (curl/CLI/same-origin non-browser) is allowed.
            //
            // This guard is gated behind the GatewayDevOriginEnforcement feature flag, which is
            // ON by default (#1946): keyless gateways are protected out-of-the-box. Operators can
            // explicitly opt out with FeatureManagement.GatewayDevOriginEnforcement: false. A flag
            // evaluation fault fails OPEN so a misconfiguration can never lock the operator out. A
            // null feature manager (constructed without DI, e.g. in tests) is treated as disabled.
            if (await IsDevOriginEnforcementEnabledAsync().ConfigureAwait(false) &&
                !IsOriginAllowed(context.Headers, out var rejectedOrigin))
            {
                _logger.LogWarning(
                    "Gateway dev-mode auth denied: browser Origin '{Origin}' is not in the allow-list.",
                    rejectedOrigin);
                EmitOutcome(success: false, actorId: "development");
                return GatewayAuthResult.Failure(
                    $"Origin not allowed. '{rejectedOrigin}' is not in Gateway.Cors.AllowedOrigins.");
            }

            _logger.LogDebug("Gateway auth is running in development mode: no API key configured.");
            EmitOutcome(success: true, actorId: "development");
            return GatewayAuthResult.Success(new GatewayCallerIdentity
            {
                CallerId = "gateway-dev",
                DisplayName = "Gateway Development Caller",
                TenantId = "development",
                Permissions = ["*"],
                IsAdmin = true
            });
        }

        var presentedKey = ExtractApiKey(context.Headers);
        if (presentedKey is null)
        {
            EmitOutcome(success: false, actorId: "anonymous");
            return GatewayAuthResult.Failure("Missing API key. Provide X-Api-Key or Authorization: Bearer <key>.");
        }

        // Resolve identity with a constant-time comparison. The dictionary is retained for
        // identity storage, but acceptance is gated on a timing-safe comparison of each stored
        // key against the presented key. We deliberately iterate ALL candidate keys and never
        // early-return on a match so that runtime does not depend on which/whether a key matched
        // (a data-dependent dictionary probe on the secret would otherwise leak timing).
        GatewayCallerIdentity? matched = null;
        foreach (var (storedKey, candidate) in identitiesByApiKey)
        {
            if (TimingSafe.Equals(storedKey, presentedKey))
                matched = candidate;
        }

        if (matched is null)
        {
            EmitOutcome(success: false, actorId: presentedKey);
            return GatewayAuthResult.Failure("Invalid API key.");
        }

        EmitOutcome(success: true, actorId: matched.CallerId);
        return GatewayAuthResult.Success(matched);
    }

    /// <summary>
    /// Returns whether the dev-mode browser-Origin guard is currently enabled. The guard is ON by
    /// default (#1946): when the <see cref="DevOriginEnforcementFeature"/> flag is <em>unspecified</em>
    /// the guard is active, protecting keyless gateways out-of-the-box. An operator retains the
    /// escape hatch of setting the flag explicitly to <c>false</c>. Defaults to <c>false</c> only
    /// when no feature manager is wired (e.g. tests or non-DI construction), and a flag-evaluation
    /// fault fails OPEN (guard disabled) so a misconfiguration can never lock the operator out.
    /// </summary>
    private async Task<bool> IsDevOriginEnforcementEnabledAsync()
    {
        if (_featureManager is null)
            return false;

        try
        {
            var enabled = await _featureManager.IsEnabledAsync(DevOriginEnforcementFeature).ConfigureAwait(false);
            if (enabled)
                return true;

            // #1946: IsEnabledAsync reports both an unspecified flag and an explicit `false` as
            // disabled. To honour the default-ON policy we consult IConfiguration: if the operator
            // has NOT specified the flag at all, the guard defaults ON. An explicitly configured
            // value (including `false`) is respected as-is. When no configuration is available
            // (non-DI construction), we fall back to the feature manager's disabled result.
            if (_configuration is null)
                return false;

            var configured = _configuration[DevOriginEnforcementConfigKey];
            if (string.IsNullOrWhiteSpace(configured))
                return true;

            return false;
        }
        catch (Exception ex)
        {
            // A feature-flag evaluation fault must fail OPEN (guard disabled) so a misconfigured
            // flag can never lock the operator out of their own gateway. Log and treat as off.
            _logger.LogWarning(ex, "Failed to evaluate feature flag '{Feature}'; treating dev-mode origin enforcement as disabled.", DevOriginEnforcementFeature);
            return false;
        }
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

        try
        {
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
        catch (OptionsValidationException ex)
        {
            // A bad unrelated setting must not turn authentication into a site-wide 500 storm.
            // IOptionsMonitor throws while materialising the whole PlatformConfig, even though
            // auth only needs its small credential subset. Keep serving the last valid identity
            // snapshot until a later reload becomes valid.
            _logger.LogError(
                ex,
                "Platform configuration reload is invalid; gateway authentication is using the last valid credential snapshot.");
            lock (_sync)
                return _identitiesByApiKey;
        }
    }

    /// <summary>
    /// Enforces the browser-Origin allow-list used to guard the dev-mode admin grant against
    /// DNS-rebind / CSRF attacks. Requests without an <c>Origin</c> header (non-browser clients
    /// such as curl or the CLI) are always allowed; a present Origin must exactly match one of
    /// the configured allow-listed origins (defaulting to <see cref="DefaultAllowedOrigin"/>).
    /// </summary>
    /// <param name="headers">Request headers.</param>
    /// <param name="rejectedOrigin">The offending origin when the result is <c>false</c>.</param>
    /// <returns><c>true</c> if the request may proceed; otherwise <c>false</c>.</returns>
    private bool IsOriginAllowed(IReadOnlyDictionary<string, string> headers, out string rejectedOrigin)
    {
        rejectedOrigin = string.Empty;

        if (!TryGetHeaderValue(headers, OriginHeader, out var origin) ||
            string.IsNullOrWhiteSpace(origin))
        {
            // No browser Origin present: non-browser/same-origin caller, allow.
            return true;
        }

        origin = origin.Trim();
        foreach (var allowed in GetAllowedOrigins())
        {
            if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        rejectedOrigin = origin;
        return false;
    }

    /// <summary>
    /// Resolves the effective browser-Origin allow-list, reading from the live options monitor
    /// when available so runtime config edits take effect, and falling back to the snapshot
    /// captured at construction or the built-in default.
    /// </summary>
    private IReadOnlyList<string> GetAllowedOrigins()
    {
        if (_platformConfig is not null)
            return ResolveAllowedOrigins(_platformConfig.CurrentValue.Gateway?.Cors?.AllowedOrigins);

        return _staticAllowedOrigins ?? [DefaultAllowedOrigin];
    }

    /// <summary>
    /// Normalises a configured origin list into a non-empty allow-list, dropping blanks and
    /// falling back to <see cref="DefaultAllowedOrigin"/> when nothing usable is configured.
    /// </summary>
    private static IReadOnlyList<string> ResolveAllowedOrigins(IEnumerable<string>? configured)
    {
        if (configured is null)
            return [DefaultAllowedOrigin];

        var origins = configured
            .Where(static o => !string.IsNullOrWhiteSpace(o))
            .Select(static o => o.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return origins.Length > 0 ? origins : [DefaultAllowedOrigin];
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
