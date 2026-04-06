using Microsoft.AspNetCore.Http;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Adds a correlation identifier to each request/response for end-to-end tracing.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    internal const string CorrelationIdHeaderName = "X-Correlation-Id";
    internal const string CorrelationIdItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
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

        await _next(context);
    }
}
