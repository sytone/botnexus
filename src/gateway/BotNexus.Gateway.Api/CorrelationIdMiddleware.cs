using Microsoft.AspNetCore.Http;
using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Adds a correlation identifier to each request/response for end-to-end tracing.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    internal const string CorrelationIdHeaderName = "X-Correlation-Id";
    internal const string CorrelationIdItemKey = "CorrelationId";
    internal const string SessionIdItemKey = "SessionId";
    internal const string AgentIdItemKey = "AgentId";
    internal const string ChannelItemKey = "Channel";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
/// <param name="logger">The logger instance.</param>
    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Populates the request context and response headers with a correlation identifier.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing middleware completion.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = System.Diagnostics.Activity.Current is { } activity
            ? activity.TraceId.ToString()
            : context.Request.Headers.TryGetValue(CorrelationIdHeaderName, out var incomingCorrelationId) &&
              !string.IsNullOrWhiteSpace(incomingCorrelationId)
                ? incomingCorrelationId.ToString()
                : Guid.NewGuid().ToString("D");

        context.Items[CorrelationIdItemKey] = correlationId;
        context.Response.Headers[CorrelationIdHeaderName] = correlationId;

        var sessionId = ResolveContextValue(context, "sessionId", "session");
        var agentId = ResolveContextValue(context, "agentId", "agent");
        var channel = ResolveContextValue(context, "channel", "channelType");

        context.Items[SessionIdItemKey] = sessionId;
        context.Items[AgentIdItemKey] = agentId;
        context.Items[ChannelItemKey] = channel;

        System.Diagnostics.Activity.Current?.SetTag("botnexus.correlation.id", correlationId);
        if (!string.IsNullOrWhiteSpace(sessionId))
            System.Diagnostics.Activity.Current?.SetTag("botnexus.session.id", sessionId);
        if (!string.IsNullOrWhiteSpace(agentId))
            System.Diagnostics.Activity.Current?.SetTag("botnexus.agent.id", agentId);
        if (!string.IsNullOrWhiteSpace(channel))
            System.Diagnostics.Activity.Current?.SetTag("botnexus.channel.type", channel);

        GatewayTelemetry.Requests.Add(1,
            new KeyValuePair<string, object?>("http.route", context.Request.Path.Value ?? "/"),
            new KeyValuePair<string, object?>("botnexus.session.id", sessionId),
            new KeyValuePair<string, object?>("botnexus.agent.id", agentId),
            new KeyValuePair<string, object?>("botnexus.channel.type", channel));

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["SessionId"] = sessionId,
            ["AgentId"] = agentId,
            ["Channel"] = channel
        }))
        {
            await _next(context);
        }
    }

    private static string? ResolveContextValue(HttpContext context, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (context.Request.RouteValues.TryGetValue(key, out var routeValue) &&
                routeValue is not null &&
                !string.IsNullOrWhiteSpace(routeValue.ToString()))
            {
                return routeValue.ToString();
            }

            if (context.Request.Query.TryGetValue(key, out var queryValue) &&
                !string.IsNullOrWhiteSpace(queryValue.ToString()))
            {
                return queryValue.ToString();
            }

            if (context.Request.Headers.TryGetValue(key, out var headerValue) &&
                !string.IsNullOrWhiteSpace(headerValue.ToString()))
            {
                return headerValue.ToString();
            }
        }

        return null;
    }
}
