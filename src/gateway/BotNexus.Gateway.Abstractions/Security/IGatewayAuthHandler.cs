namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Authenticates API and WebSocket requests to the Gateway.
/// Implementations validate credentials and return a caller identity.
/// </summary>
/// <remarks>
/// <para>Built into the Gateway API middleware pipeline. Each request passes through
/// authentication before reaching controllers or WebSocket handlers.</para>
/// <para>Built-in implementations (planned):</para>
/// <list type="bullet">
///   <item><b>ApiKeyAuthHandler</b> — Static API key validation. Suitable for development
///   and simple single-tenant deployments.</item>
///   <item><b>JwtAuthHandler</b> — JWT bearer token validation. For production multi-tenant
///   scenarios. Phase 2.</item>
/// </list>
/// </remarks>
public interface IGatewayAuthHandler
{
    /// <summary>
    /// The authentication scheme name (e.g., "ApiKey", "Bearer").
    /// </summary>
    string Scheme { get; }

    /// <summary>
    /// Attempts to authenticate a request from its headers/query parameters.
    /// </summary>
    /// <param name="context">The authentication context with request details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authentication result, which is either authenticated or denied.</returns>
    Task<GatewayAuthResult> AuthenticateAsync(GatewayAuthContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Context provided to the authentication handler for each request.
/// </summary>
public sealed record GatewayAuthContext
{
    /// <summary>Request headers as key-value pairs.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Query string parameters.</summary>
    public required IReadOnlyDictionary<string, string> QueryParameters { get; init; }

    /// <summary>The request path.</summary>
    public required string Path { get; init; }

    /// <summary>The HTTP method (GET, POST, etc.) or "WS" for WebSocket.</summary>
    public required string Method { get; init; }
}

/// <summary>
/// Result of an authentication attempt.
/// </summary>
public sealed record GatewayAuthResult
{
    /// <summary>Whether the request was authenticated.</summary>
    public required bool IsAuthenticated { get; init; }

    /// <summary>The authenticated caller identity, if successful.</summary>
    public GatewayCallerIdentity? Identity { get; init; }

    /// <summary>Failure reason, if authentication failed.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Creates a successful authentication result.</summary>
    public static GatewayAuthResult Success(GatewayCallerIdentity identity) =>
        new() { IsAuthenticated = true, Identity = identity };

    /// <summary>Creates a failed authentication result.</summary>
    public static GatewayAuthResult Failure(string reason) =>
        new() { IsAuthenticated = false, FailureReason = reason };
}

/// <summary>
/// Represents an authenticated caller's identity and permissions.
/// </summary>
public sealed record GatewayCallerIdentity
{
    /// <summary>Unique caller identifier.</summary>
    public required string CallerId { get; init; }

    /// <summary>Human-readable caller name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Agent IDs this caller is allowed to interact with.
    /// Empty means all agents are accessible.
    /// </summary>
    public IReadOnlyList<string> AllowedAgents { get; init; } = [];

    /// <summary>Whether this caller has administrative privileges.</summary>
    public bool IsAdmin { get; init; }
}
