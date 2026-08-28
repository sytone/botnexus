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

        // A cooperatively cancelled start or loop is a shutdown signal, never something to retry,
        // so an OperationCanceledException at the head of the chain is handled before the general
        // walk. It cannot simply be classified Terminal on type alone: HttpClient surfaces its own
        // transport failures as TaskCanceledException too, and those are the most transient faults
        // a long-polling channel can produce.
        //
        // Earlier revisions answered "is this a timeout?" by hunting for a TimeoutException marker
        // (#3116). That marker is set only on the HttpClient.Timeout path; a host sleep/resume
        // produces TaskCanceledException -> HttpRequestException -> IOException ->
        // SocketException(ConnectionReset) with no marker anywhere, so the guard returned Terminal
        // and parked both Telegram polling loops for hours (#3630). Ask the question the guard
        // actually means instead: does the chain carry a cause this classifier already recognises
        // as transient? Genuine cooperative cancellation carries no such cause and still fails
        // closed to Terminal.
        if (exception is OperationCanceledException)
        {
            return HasTransientCause(exception)
                ? ChannelFailureKind.Transient
                : ChannelFailureKind.Terminal;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (ClassifyCause(current) is { } verdict)
                return verdict;

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
    /// Classifies a single link of an exception chain, or returns <see langword="null"/> when this
    /// link carries no verdict and the walk should continue.
    /// </summary>
    /// <remarks>
    /// Shared by the general walk and the cancellation guard so the two can never disagree about
    /// the same cause - the disagreement that made #3630 possible, where the guard called a
    /// <see cref="SocketException"/> chain Terminal while the walk below it already called the same
    /// socket error Transient.
    /// </remarks>
    private static ChannelFailureKind? ClassifyCause(Exception exception) => exception switch
    {
        // A status-bearing HttpRequestException is authoritative. Without a status the request
        // failed below HTTP, so keep walking for the transport fault underneath rather than
        // assuming the worst here.
        HttpRequestException { StatusCode: { } status } => ClassifyStatus(status),

        SocketException socket => IsTransientSocketError(socket.SocketErrorCode)
            ? ChannelFailureKind.Transient
            : ChannelFailureKind.Terminal,

        TimeoutException or IOException => ChannelFailureKind.Transient,

        _ => null,
    };

    /// <summary>
    /// Determines whether a cancellation was caused by an underlying transport fault rather than by
    /// a cooperative <see cref="CancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// Cooperative cancellation is raised by the token alone and carries no transport cause, so an
    /// inner chain containing any cause this classifier recognises as transient - a
    /// <see cref="TimeoutException"/> (#3116), an <see cref="IOException"/>, a transient
    /// <see cref="SocketException"/> (#3630), a retryable HTTP status, or an SDK fault declaring
    /// <c>IsTransient</c> - means the operation died on the wire and can plausibly recover.
    /// </remarks>
    private static bool HasTransientCause(Exception exception)
    {
        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (ClassifyCause(current) == ChannelFailureKind.Transient)
                return true;

            if (TryReadIsTransient(current) == true)
                return true;
        }

        return false;
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
