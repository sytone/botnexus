using System.Text.Json;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api;

public sealed class GatewayAuthMiddleware
{
    internal const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly IGatewayAuthHandler _authHandler;
    private readonly ILogger<GatewayAuthMiddleware> _logger;

    public GatewayAuthMiddleware(
        RequestDelegate next,
        IGatewayAuthHandler authHandler,
        ILogger<GatewayAuthMiddleware> logger)
    {
        _next = next;
        _authHandler = authHandler;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldSkipAuth(context.Request))
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
        await _next(context);
    }

    private static bool ShouldSkipAuth(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/webui", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.HasExtension(path.Value);
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
