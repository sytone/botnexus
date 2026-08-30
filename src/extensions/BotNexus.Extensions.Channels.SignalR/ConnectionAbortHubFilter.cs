using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Central seam that distinguishes a client-driven connection abort from a genuine hub fault, so
/// ordinary browser churn stops manufacturing Error-level log noise (issue #3679).
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="GatewayHub"/> verb threads <c>Context.ConnectionAborted</c> into its store and
/// supervisor calls, which is correct: a client that navigates away should not keep a SQLite read
/// alive. ASP.NET Core signals that token on a <i>normal</i> disconnect, so the store dutifully
/// throws <see cref="OperationCanceledException"/>, the exception escapes the hub method, and
/// SignalR's own <c>DefaultHubDispatcher</c> logs it as
/// <c>Failed to invoke hub method '{name}'</c> at <see cref="LogLevel.Error"/>. The throw is the
/// token doing its job; the Error level is the defect. In the 24h window ending 2026-08-30 that
/// signature was 7% of all gateway errors, and it is byte-identical to a real session-store
/// outage — so a genuine failure would be dismissed as routine churn.
/// </para>
/// <para>
/// This filter absorbs the cancellation only when it can be attributed to the connection's own
/// abort token, logs it at <see cref="LogLevel.Debug"/>, and returns <see langword="null"/> — a
/// completion that is never transmitted, because the connection is already gone. This mirrors
/// <c>RequestCancellationMiddleware</c> on the HTTP side (#2387), including its deliberate refusal
/// to be a blanket catch: an internal timeout, a rogue linked token, or any non-cancellation
/// exception is rethrown untouched and still surfaces at Error with its stack attached.
/// </para>
/// <para>
/// Registering it as a <i>global</i> hub filter is what makes the audit clause of #3679 structural
/// rather than enumerated: every current and future hub verb that passes
/// <c>Context.ConnectionAborted</c> into a store call is routed through this one seam, so no
/// sibling verb can regress the behaviour by being overlooked.
/// </para>
/// </remarks>
public sealed class ConnectionAbortHubFilter : IHubFilter
{
    private readonly ILogger<ConnectionAbortHubFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionAbortHubFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger used to record absorbed client aborts at Debug level.</param>
    public ConnectionAbortHubFilter(ILogger<ConnectionAbortHubFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (OperationCanceledException ex)
            when (IsConnectionAbort(invocationContext.Context.ConnectionAborted, ex))
        {
            _logger.LogDebug(
                "Hub method '{HubMethod}' was abandoned by the client on connection {ConnectionId}; " +
                "the connection aborted mid-invocation.",
                invocationContext.HubMethodName,
                invocationContext.Context.ConnectionId);

            // The connection is gone, so no completion reaches anyone. Returning null keeps the
            // dispatcher on its success path and off the "failed to invoke hub method" logger.
            return null;
        }
    }

    /// <summary>
    /// Determines whether a cancellation can be attributed to the client aborting its connection,
    /// as opposed to an internal cancellation that must still be reported as a fault.
    /// </summary>
    /// <param name="connectionAborted">The connection's abort token.</param>
    /// <param name="exception">The observed cancellation.</param>
    /// <returns><see langword="true"/> only when the client abort explains the cancellation.</returns>
    /// <remarks>
    /// The authoritative signal is the token carried by the exception matching
    /// <paramref name="connectionAborted"/>. Framework and ADO.NET paths frequently surface a
    /// default or linked token instead; those are attributed to the client only when no other
    /// token was signalled, because an internal timeout leaves <paramref name="connectionAborted"/>
    /// unsignalled and must not be silenced by this filter.
    /// </remarks>
    internal static bool IsConnectionAbort(CancellationToken connectionAborted, OperationCanceledException exception)
    {
        if (exception.CancellationToken == connectionAborted)
            return connectionAborted.IsCancellationRequested;

        return !exception.CancellationToken.IsCancellationRequested && connectionAborted.IsCancellationRequested;
    }
}
