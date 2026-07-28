using System.Net;
using System.Net.Sockets;

namespace BotNexus.Gateway.Channels.Startup;

/// <summary>
/// Shared classifier that decides whether a channel failure is worth retrying.
/// </summary>
/// <remarks>
/// <para>
/// Introduced for the adapter <em>startup</em> path (#2447), where a single transient upstream
/// 502 during <c>StartAsync</c> permanently disabled a channel for the life of the process
/// because start was one-shot. The fix retries - but only failures that can plausibly clear.
/// Blindly retrying a revoked bot token just burns attempts and hides the real problem.
/// </para>
/// <para>
/// The steady-state polling loops tracked by #2386 have the mirror-image defect (unbounded
/// retry of everything, including terminal faults). They are expected to consume this same
/// classifier when that work lands, which is why this type lives in the shared
/// <c>BotNexus.Gateway.Channels</c> assembly rather than inside a single adapter.
/// </para>
/// </remarks>
public static class ChannelFailureClassifier
{
    /// <summary>
    /// Classifies an exception raised while starting or running a channel adapter.
    /// </summary>
    /// <param name="exception">The failure to classify.</param>
    /// <returns>
    /// <see cref="ChannelFailureKind.Transient"/> when a retry may succeed;
    /// <see cref="ChannelFailureKind.Terminal"/> otherwise.
    /// </returns>
    /// <remarks>
    /// Unrecognised exception types are classified <see cref="ChannelFailureKind.Terminal"/>.
    /// Failing closed keeps a genuinely broken adapter from looping: the retry budget is a
    /// concession to known-momentary faults, not a default.
    /// </remarks>
    public static ChannelFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // A cancelled start is a shutdown signal, never something to retry. This must be
                // checked before TaskCanceledException's transient-timeout treatment below.
                case OperationCanceledException when exception is OperationCanceledException:
                    return ChannelFailureKind.Terminal;

                // A status-bearing HttpRequestException is authoritative. Without a status the
                // request failed below HTTP, so keep walking for the transport fault underneath
                // rather than assuming the worst here.
                case HttpRequestException { StatusCode: { } status }:
                    return ClassifyStatus(status);

                case SocketException socket:
                    return IsTransientSocketError(socket.SocketErrorCode)
                        ? ChannelFailureKind.Transient
                        : ChannelFailureKind.Terminal;

                case TimeoutException:
                case IOException:
                    return ChannelFailureKind.Transient;
            }

            // Azure SDK faults (ServiceBusException, RequestFailedException derivatives) carry
            // their own retryability verdict on a public bool IsTransient. Honour it via the
            // convention rather than referencing an Azure package here - this assembly must stay
            // free of channel-specific types (#2386). Only a positive verdict short-circuits;
            // IsTransient == false keeps walking and ultimately falls through to Terminal, so
            // the fail-closed default is preserved.
            if (TryReadIsTransient(current) == true)
                return ChannelFailureKind.Transient;
        }

        return ChannelFailureKind.Terminal;
    }

    /// <summary>
    /// Classifies an HTTP status code. 5xx, 408 and 429 are momentary; 4xx credential/config
    /// faults are deterministic.
    /// </summary>
    /// <param name="status">The status code, or <see langword="null"/> when the transport
    /// failed before a response was produced.</param>
    /// <returns>The classification for <paramref name="status"/>.</returns>
    public static ChannelFailureKind ClassifyStatus(HttpStatusCode? status) => status switch
    {
        // No response at all: the request never completed. Only treat it as transient when an
        // inner transport fault says so, which the caller-side walk in Classify already handles;
        // on its own an unqualified failure is not assumed retryable.
        null => ChannelFailureKind.Terminal,
        HttpStatusCode.RequestTimeout => ChannelFailureKind.Transient,      // 408
        HttpStatusCode.TooManyRequests => ChannelFailureKind.Transient,     // 429
        HttpStatusCode.MisdirectedRequest => ChannelFailureKind.Transient,  // 421
        >= HttpStatusCode.InternalServerError => ChannelFailureKind.Transient, // 5xx
        _ => ChannelFailureKind.Terminal,
    };

    /// <summary>
    /// Reads a public instance <c>bool IsTransient</c> property when the exception exposes one.
    /// </summary>
    /// <returns>The declared verdict, or <see langword="null"/> when no such property exists.</returns>
    private static bool? TryReadIsTransient(Exception exception)
    {
        var property = exception.GetType().GetProperty(
            "IsTransient",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        if (property is null || property.PropertyType != typeof(bool) || !property.CanRead)
            return null;

        return property.GetValue(exception) as bool?;
    }

    private static bool IsTransientSocketError(SocketError error) => error is
        SocketError.ConnectionReset or
        SocketError.ConnectionAborted or
        SocketError.ConnectionRefused or
        SocketError.TimedOut or
        SocketError.HostUnreachable or
        SocketError.HostNotFound or
        SocketError.NetworkUnreachable or
        SocketError.TryAgain;
}
