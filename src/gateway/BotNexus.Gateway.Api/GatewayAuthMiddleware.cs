using System.Text.Json;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;

namespace BotNexus.Gateway.Api;

/// <summary>
/// ASP.NET Core middleware that enforces authentication and authorization for Gateway requests.
/// Validates caller identity and agent access permissions before allowing requests to proceed.
/// </summary>
public sealed class GatewayAuthMiddleware
{
    internal const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";

    /// <summary>
    /// Feature flag gating permission ENFORCEMENT (#2621). Off by default: while off the scope
    /// decision is still evaluated and every would-be refusal is logged, so an operator can learn
    /// their callers' real scope set before a denial can break anything.
    /// </summary>
    public const string PermissionEnforcementFeature = FeatureFlags.GatewayPermissionEnforcement;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly IGatewayAuthHandler _authHandler;
    private readonly IFileProvider? _webRootFileProvider;
    private readonly ILogger<GatewayAuthMiddleware> _logger;
    private readonly IFeatureManager? _featureManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayAuthMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="authHandler">The authentication handler for verifying caller credentials.</param>
    /// <param name="webHostEnvironment">The host environment used for web-root static file checks.</param>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    /// <param name="featureManager">
    /// Feature manager used to evaluate <see cref="PermissionEnforcementFeature"/>. A null manager
    /// (non-DI construction, e.g. tests) means enforcement is off and only audit logging runs.
    /// </param>
    public GatewayAuthMiddleware(
        RequestDelegate next,
        IGatewayAuthHandler authHandler,
        IWebHostEnvironment webHostEnvironment,
        ILogger<GatewayAuthMiddleware> logger,
        IFeatureManager? featureManager = null)
    {
        _next = next;
        _authHandler = authHandler;
        _webRootFileProvider = webHostEnvironment.WebRootFileProvider;
        _logger = logger;
        _featureManager = featureManager;
    }

    /// <summary>
    /// Processes an HTTP request to verify authentication and authorization.
    /// If authentication fails, returns a 401 or 403 response without invoking the next middleware.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task that represents the asynchronous middleware operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldSkipAuth(context.Request, _webRootFileProvider))
        {
            await _next(context);
            return;
        }

        var authContext = new GatewayAuthContext
        {
            Headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            QueryParameters = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            Path = context.Request.Path.Value ?? string.Empty,
            Method = context.WebSockets.IsWebSocketRequest ? "WS" : context.Request.Method
        };

        var authResult = await _authHandler.AuthenticateAsync(authContext, context.RequestAborted);
        if (!authResult.IsAuthenticated)
        {
            _logger.LogWarning("Gateway request denied: {Path}. Reason: {Reason}", context.Request.Path, authResult.FailureReason);
            await WriteErrorAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "unauthenticated",
                authResult.FailureReason ?? "Authentication failed.");
            return;
        }

        var identity = authResult.Identity;
        if (identity is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "forbidden", "Caller is not authorized.");
            return;
        }

        var requestedAgentId = await ExtractRequestedAgentIdAsync(context.Request, context.RequestAborted);
        if (!IsAgentAuthorized(identity, requestedAgentId))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "forbidden",
                $"Caller is not authorized for agent '{requestedAgentId}'.");
            return;
        }

        context.Items[CallerIdentityItemKey] = identity;

        if (!await IsPermittedAsync(context, identity, authContext.Method))
            return;

        await _next(context);
    }

    /// <summary>
    /// Evaluates the caller's permissions against the scope this request requires (#2621), writes
    /// the 403 when enforcement is on and the caller is short, and returns whether the pipeline may
    /// continue.
    /// <para>
    /// The decision is computed on EVERY authenticated request regardless of the flag. That is
    /// deliberate: with the flag off the outcome is logged rather than applied, which is the only
    /// way an operator can discover what enforcement would break before enabling it. A flag that
    /// merely skipped the computation would leave them guessing.
    /// </para>
    /// </summary>
    private async Task<bool> IsPermittedAsync(HttpContext context, GatewayCallerIdentity identity, string method)
    {
        var requiredScope = GatewayScopes.Resolve(context.Request.Path.Value, method);

        if (GatewayScopes.IsAuthorized(identity.Permissions, requiredScope))
            return true;

        // Null means the path matched no known resource. It is reported distinctly because the two
        // causes need different operator responses: an unmapped route is a coverage gap in
        // GatewayScopes, a mapped one is a genuinely under-scoped caller.
        var scopeForLog = requiredScope ?? "(unmapped path)";
        var enforcing = await IsEnforcementEnabledAsync();

        if (!enforcing)
        {
            _logger.LogWarning(
                "Gateway permission audit: caller '{CallerId}' lacks scope '{RequiredScope}' for {Method} {Path} "
                + "and would be REFUSED once '{Feature}' is enabled. Granted scopes: [{Granted}]. "
                + "No action taken - enforcement is currently off.",
                identity.CallerId,
                scopeForLog,
                method,
                context.Request.Path,
                PermissionEnforcementFeature,
                string.Join(", ", identity.Permissions));
            return true;
        }

        _logger.LogWarning(
            "Gateway permission denied: caller '{CallerId}' lacks scope '{RequiredScope}' for {Method} {Path}. "
            + "Granted scopes: [{Granted}].",
            identity.CallerId,
            scopeForLog,
            method,
            context.Request.Path,
            string.Join(", ", identity.Permissions));

        await WriteErrorAsync(
            context,
            StatusCodes.Status403Forbidden,
            "permission_denied",
            $"Caller '{identity.CallerId}' does not hold the required permission scope '{scopeForLog}'.");

        return false;
    }

    /// <summary>
    /// Returns whether permission enforcement is enabled. Every fallback is deliberately
    /// enforcement-OFF, matching the dev-origin guard: an authorization control that turns itself
    /// on because a feature-flag provider faulted would lock an operator out of their own gateway.
    /// Note this is NOT a fail-open authorization decision - the scope check itself fails closed
    /// (an unknown scope grants nothing); this only governs whether the rollout is live.
    /// </summary>
    private async Task<bool> IsEnforcementEnabledAsync()
    {
        if (_featureManager is null)
            return false;

        try
        {
            return await _featureManager.IsEnabledAsync(PermissionEnforcementFeature);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to evaluate feature flag '{Feature}'; treating gateway permission enforcement as disabled.",
                PermissionEnforcementFeature);
            return false;
        }
    }

    private static bool ShouldSkipAuth(HttpRequest request, IFileProvider? webRootFileProvider)
    {
        var path = request.Path;
        return path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/api/federation/cross-world", StringComparison.OrdinalIgnoreCase) ||
               // Webhook inbound endpoints use HMAC-SHA256 token auth, not the gateway API key.
               // The path pattern is /api/webhooks/{agentId}/{webhookId} (POST only).
               (HttpMethods.IsPost(request.Method) &&
                path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWithSegments("/api/webhooks/registrations", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWithSegments("/api/webhooks/runs", StringComparison.OrdinalIgnoreCase)) ||
               IsStaticWebRootFile(request, webRootFileProvider);
    }

    private static bool IsStaticWebRootFile(HttpRequest request, IFileProvider? webRootFileProvider)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        if (webRootFileProvider is null)
            return false;

        var relativePath = request.Path.Value?.TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var fileInfo = webRootFileProvider.GetFileInfo(relativePath);
        return fileInfo.Exists && !fileInfo.IsDirectory;
    }

    private static bool IsAgentAuthorized(GatewayCallerIdentity identity, string? requestedAgentId)
    {
        if (identity.IsAdmin || identity.AllowedAgents.Count == 0 || string.IsNullOrWhiteSpace(requestedAgentId))
            return true;

        return identity.AllowedAgents.Any(agent =>
            string.Equals(agent, requestedAgentId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> ExtractRequestedAgentIdAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Query.TryGetValue("agent", out var agentQueryValue))
        {
            var agentId = agentQueryValue.ToString();
            if (!string.IsNullOrWhiteSpace(agentId))
                return agentId;
        }

        if (request.RouteValues.TryGetValue("agentId", out var routeAgentId) &&
            routeAgentId is string routeAgentString &&
            !string.IsNullOrWhiteSpace(routeAgentString))
        {
            return routeAgentString;
        }

        if (request.Path.StartsWithSegments("/api/chat", StringComparison.OrdinalIgnoreCase) &&
            HttpMethods.IsPost(request.Method) &&
            request.ContentLength > 0)
        {
            request.EnableBuffering();
            request.Body.Position = 0;

            try
            {
                using var payload = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                if (payload.RootElement.ValueKind == JsonValueKind.Object &&
                    payload.RootElement.TryGetProperty("agentId", out var agentIdElement))
                {
                    var bodyAgentId = agentIdElement.GetString();
                    if (!string.IsNullOrWhiteSpace(bodyAgentId))
                        return bodyAgentId;
                }
            }
            catch (JsonException)
            {
            }
            finally
            {
                request.Body.Position = 0;
            }
        }

        return null;
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string error, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new GatewayErrorResponse(error, message), JsonOptions),
            context.RequestAborted);
    }

    private sealed record GatewayErrorResponse(string Error, string Message);
}
