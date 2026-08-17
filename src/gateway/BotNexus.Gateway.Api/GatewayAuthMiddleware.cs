using System.Text.Json;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api;

/// <summary>
/// ASP.NET Core middleware that enforces authentication and authorization for Gateway requests.
/// Validates caller identity and agent access permissions before allowing requests to proceed.
/// </summary>
public sealed class GatewayAuthMiddleware
{
    internal const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly IGatewayAuthHandler _authHandler;
    private readonly IFileProvider? _webRootFileProvider;
    private readonly ILogger<GatewayAuthMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayAuthMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="authHandler">The authentication handler for verifying caller credentials.</param>
    /// <param name="webHostEnvironment">The host environment used for web-root static file checks.</param>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    public GatewayAuthMiddleware(
        RequestDelegate next,
        IGatewayAuthHandler authHandler,
        IWebHostEnvironment webHostEnvironment,
        ILogger<GatewayAuthMiddleware> logger)
    {
        _next = next;
        _authHandler = authHandler;
        _webRootFileProvider = webHostEnvironment.WebRootFileProvider;
        _logger = logger;
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

        if (RequiresAdminScope(context.Request) && !identity.IsAdmin)
        {
            _logger.LogWarning(
                "Admin-scope denied for caller '{CallerId}' on {Method} {Path}.",
                identity.CallerId,
                context.Request.Method,
                context.Request.Path);
            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "forbidden",
                "Caller does not have administrative scope for this endpoint.");
            return;
        }

        context.Items[CallerIdentityItemKey] = identity;
        await _next(context);
    }

    /// <summary>
    /// Returns whether the request targets a platform-administration endpoint that requires the
    /// caller to hold <see cref="GatewayCallerIdentity.IsAdmin"/> (issue #506).
    /// </summary>
    /// <remarks>
    /// <para>
    /// #506 proposed an ASP.NET <c>[Authorize(Policy = "AdminScope")]</c> attribute. The gateway
    /// runs no ASP.NET authentication stack -- this middleware is the sole authentication and
    /// authorization seam for <c>/api/*</c> -- so adding a policy stack would create a second,
    /// competing gate that could silently disagree with this one. The scope is instead enforced
    /// here against the identity flag the middleware already resolves, which is the same admin
    /// gate <c>SecurityDiagnosticsController</c> uses.
    /// </para>
    /// <para>
    /// The gate is keyed on the METHOD as well as the path. Reads of <c>/api/config</c> remain
    /// open to any authenticated caller because the portal and the CLI both depend on them and
    /// the whole-config read already masks secrets; only the mutating verbs are administration.
    /// <see cref="PathString.StartsWithSegments(PathString, StringComparison)"/> is used rather
    /// than a string prefix so a sibling route such as <c>/api/configuration-notes</c> cannot be
    /// captured by the gate.
    /// </para>
    /// </remarks>
    private static bool RequiresAdminScope(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) ||
            HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method))
        {
            return false;
        }

        return request.Path.StartsWithSegments("/api/config", StringComparison.OrdinalIgnoreCase);
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
