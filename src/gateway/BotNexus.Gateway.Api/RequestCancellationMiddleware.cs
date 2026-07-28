using Microsoft.AspNetCore.Http;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Central seam that distinguishes client-driven request aborts from genuine internal cancellation
/// (see issue #2387).
/// </summary>
/// <remarks>
/// <para>
/// When a client disconnects mid-flight, ASP.NET Core signals <see cref="HttpContext.RequestAborted"/>
/// and the resulting <see cref="OperationCanceledException"/> used to propagate out of the MVC
/// pipeline, where it was logged at Error level and reported as HTTP 500. That is log noise, not a
/// server fault.
/// </para>
/// <para>
/// This middleware compares the cancelled token to <see cref="HttpContext.RequestAborted"/>. Only when
/// the request itself was aborted is the exception absorbed, logged at Debug, and the response marked
/// with the nginx-style 499 status (which is never transmitted - the connection is already gone).
/// Any other cancellation, including an internal timeout or a rogue token, is rethrown so it still
/// surfaces as an error. This is deliberately not a blanket catch.
/// </para>
/// </remarks>
public sealed class RequestCancellationMiddleware
{
    /// <summary>
    /// Non-standard status code (nginx convention) meaning "client closed request".
    /// </summary>
    public const int ClientClosedRequestStatusCode = 499;

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestCancellationMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestCancellationMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">The logger instance.</param>
    public RequestCancellationMiddleware(RequestDelegate next, ILogger<RequestCancellationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the next middleware, absorbing only request-abort cancellation.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing middleware completion.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException ex) when (IsRequestAbort(context, ex))
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequestStatusCode;
            }

            _logger.LogDebug(
                "Request {Method} {Path} was aborted by the client; responding {StatusCode}.",
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                ClientClosedRequestStatusCode);
        }
    }

    /// <summary>
    /// Determines whether the supplied cancellation originated from the client aborting the request.
    /// </summary>
    private static bool IsRequestAbort(HttpContext context, OperationCanceledException exception)
    {
        var requestAborted = context.RequestAborted;

        // The authoritative signal is the token carried by the exception matching RequestAborted.
        if (exception.CancellationToken == requestAborted)
        {
            return requestAborted.IsCancellationRequested;
        }

        // Some framework paths surface a linked or default token. Only treat those as an abort when
        // the request really was aborted; an internal cancellation leaves RequestAborted unsignalled.
        return !exception.CancellationToken.IsCancellationRequested && requestAborted.IsCancellationRequested;
    }
}
