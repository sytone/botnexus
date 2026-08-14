using System.Net;
using System.Net.Sockets;
using BotNexus.Gateway.Channels.Startup;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// Covers the shared transient/terminal classification introduced for #2447 and intended for
/// reuse by the polling-loop bounding work in #2386.
/// </summary>
public sealed class ChannelFailureClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]           // 502 - the observed incident
    [InlineData(HttpStatusCode.ServiceUnavailable)]   // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]       // 504
    [InlineData(HttpStatusCode.InternalServerError)]  // 500
    [InlineData(HttpStatusCode.RequestTimeout)]       // 408
    [InlineData(HttpStatusCode.TooManyRequests)]      // 429
    public void Classify_HttpServerAndTimeoutStatuses_AreTransient(HttpStatusCode status)
    {
        var ex = new HttpRequestException("boom", null, status);

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Transient);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]  // 401 - revoked/invalid token
    [InlineData(HttpStatusCode.Forbidden)]     // 403
    [InlineData(HttpStatusCode.NotFound)]      // 404
    [InlineData(HttpStatusCode.BadRequest)]    // 400 - malformed request/config
    public void Classify_ClientErrorStatuses_AreTerminal(HttpStatusCode status)
    {
        var ex = new HttpRequestException("nope", null, status);

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Terminal);
    }

    [Fact]
    public void Classify_TimeoutException_IsTransient()
        => ChannelFailureClassifier.Classify(new TimeoutException()).ShouldBe(ChannelFailureKind.Transient);

    [Fact]
    public void Classify_SocketReset_IsTransient()
        => ChannelFailureClassifier
            .Classify(new SocketException((int)SocketError.ConnectionReset))
            .ShouldBe(ChannelFailureKind.Transient);

    [Fact]
    public void Classify_SocketResetWrappedInHttpRequestException_IsTransient()
    {
        // The real transport shape: HttpRequestException with NO status, wrapping a socket fault.
        var ex = new HttpRequestException("transport", new SocketException((int)SocketError.ConnectionReset));

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Transient);
    }

    [Fact]
    public void Classify_MalformedConfiguration_IsTerminal()
        => ChannelFailureClassifier
            .Classify(new InvalidOperationException("Telegram bot 'keel' requires BotToken."))
            .ShouldBe(ChannelFailureKind.Terminal);

    [Fact]
    public void Classify_Cancellation_IsTerminal()
        => ChannelFailureClassifier
            .Classify(new OperationCanceledException())
            .ShouldBe(ChannelFailureKind.Terminal);

    /// <summary>
    /// #3116 AC1 - the HttpClient.Timeout shape. HttpClient surfaces its own 100-second request
    /// timeout as a <see cref="TaskCanceledException"/>, identical in type to a cooperative
    /// cancellation; since .NET 6 the timeout case is distinguished by a
    /// <see cref="TimeoutException"/> inner. A long-poll that exceeded its client timeout is the
    /// most transient failure a long-polling transport can produce, and classifying it Terminal
    /// took Telegram bot 'keel' dark until the gateway was restarted.
    /// </summary>
    [Fact]
    public void Classify_HttpClientTimeoutTaskCanceled_IsTransient()
    {
        var ex = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Transient);
    }

    /// <summary>
    /// #3116 AC1 - the verbatim nested chain from the 2026-08-13 live trace:
    /// TaskCanceledException -&gt; TimeoutException -&gt; TaskCanceledException -&gt; IOException
    /// -&gt; SocketException(995, OperationAborted). Note the innermost socket error is NOT in the
    /// transient socket allow-list, so this must be classified from the timeout shape rather than
    /// by accidentally walking to the bottom of the chain.
    /// </summary>
    [Fact]
    public void Classify_LiveHttpClientTimeoutChain_IsTransient()
    {
        var socket = new SocketException((int)SocketError.OperationAborted); // 995
        var io = new IOException("Unable to read data from the transport connection.", socket);
        var innerCanceled = new TaskCanceledException("The operation was canceled.", io);
        var timeout = new TimeoutException("The operation was canceled.", innerCanceled);
        var ex = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            timeout);

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Transient);
    }

    /// <summary>
    /// #3116 AC2 - a cooperative cancellation carries no <see cref="TimeoutException"/> inner and
    /// must stay Terminal, so gateway shutdown is never retried. Pinned separately from the AC1
    /// tests: neither branch may be satisfied by weakening the other.
    /// </summary>
    [Fact]
    public void Classify_CooperativeTaskCanceled_IsTerminal()
    {
        var ex = new TaskCanceledException("A task was canceled.");

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Terminal);
    }

    /// <summary>
    /// #3116 AC2 - a cooperative cancellation whose inner chain contains a transport fault is
    /// still shutdown, not a timeout. Without a <see cref="TimeoutException"/> marker the walk
    /// must not be allowed to reach the transient IOException arm below.
    /// </summary>
    [Fact]
    public void Classify_CooperativeTaskCanceledWrappingTransportFault_IsTerminal()
    {
        var ex = new TaskCanceledException(
            "A task was canceled.",
            new IOException("connection closed", new SocketException((int)SocketError.ConnectionReset)));

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Terminal);
    }

    /// <summary>
    /// #3116 AC4 - the fail-closed default survives the fix: an unrecognised exception type with
    /// no recognised inner is still Terminal. The retry budget is a concession to known-momentary
    /// faults, not a default.
    /// </summary>
    [Fact]
    public void Classify_UnknownException_FailsClosedAsTerminal()
        => ChannelFailureClassifier
            .Classify(new NotSupportedException())
            .ShouldBe(ChannelFailureKind.Terminal);

    [Fact]
    public void Classify_UnknownExceptionWrappingUnknownInner_FailsClosedAsTerminal()
        => ChannelFailureClassifier
            .Classify(new NotSupportedException("outer", new FormatException("inner")))
            .ShouldBe(ChannelFailureKind.Terminal);

    /// <summary>
    /// #2386 - the Service Bus receive loop is now parked on a terminal classification, so an
    /// SDK fault that self-describes as retryable must NOT be read as terminal or a momentary
    /// broker communication blip would take the transport down until a restart. The Azure SDK
    /// convention for this is a public <c>bool IsTransient</c> property (ServiceBusException,
    /// RequestFailedException derivatives); the classifier honours the convention without
    /// referencing any Azure or channel-specific type.
    /// </summary>
    [Fact]
    public void Classify_SdkExceptionDeclaringItselfTransient_IsTransient()
        => ChannelFailureClassifier
            .Classify(new SelfDescribingException(isTransient: true))
            .ShouldBe(ChannelFailureKind.Transient);

    [Fact]
    public void Classify_SdkExceptionDeclaringItselfNonTransient_IsTerminal()
        => ChannelFailureClassifier
            .Classify(new SelfDescribingException(isTransient: false))
            .ShouldBe(ChannelFailureKind.Terminal);

    [Fact]
    public void Classify_TransientSdkExceptionNestedInsideAnotherException_IsTransient()
        => ChannelFailureClassifier
            .Classify(new InvalidOperationException("wrapper", new SelfDescribingException(isTransient: true)))
            .ShouldBe(ChannelFailureKind.Transient);

    /// <summary>Stands in for Azure's <c>ServiceBusException</c>, which is not referenced here.</summary>
    private sealed class SelfDescribingException(bool isTransient) : Exception("sdk fault")
    {
        public bool IsTransient { get; } = isTransient;
    }

    [Fact]
    public void Classify_NullException_Throws()
        => Should.Throw<ArgumentNullException>(() => ChannelFailureClassifier.Classify(null!));
}
