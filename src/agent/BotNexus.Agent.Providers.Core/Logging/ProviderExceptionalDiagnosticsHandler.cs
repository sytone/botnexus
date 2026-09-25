using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Logging;

/// <summary>
/// Emits one payload-free warning for a provider call whose terminal HTTP response is a server
/// failure. This handler is deliberately outside the retry handler: intermediate attempts remain
/// retry telemetry, while operators receive one terminal event for the logical HTTP call.
/// </summary>
public sealed class ProviderExceptionalDiagnosticsHandler(
    ILogger<ProviderExceptionalDiagnosticsHandler> logger) : DelegatingHandler
{
    /// <summary>Stable event identity for payload-free terminal provider HTTP failures.</summary>
    public static readonly EventId TerminalFailureEventId = new(4328, "ProviderHttpTerminalFailure");

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (IsExceptionalServerFailure(response.StatusCode))
        {
            logger.LogWarning(
                TerminalFailureEventId,
                "Provider HTTP terminal server failure: Status={Status} Method={Method} EndpointHost={EndpointHost} ElapsedMs={ElapsedMs}",
                (int)response.StatusCode,
                request.Method.Method,
                GetSafeEndpointHost(request.RequestUri),
                stopwatch.ElapsedMilliseconds);
        }

        return response;
    }

    private static bool IsExceptionalServerFailure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
        || (int)statusCode == 524;

    private static string GetSafeEndpointHost(Uri? requestUri) =>
        requestUri is { IsAbsoluteUri: true } ? requestUri.Host : "unknown";
}
