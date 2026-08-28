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
    /// #3630 AC1 - <b>supersedes the #3116 assertion that this same chain is Terminal.</b> That
    /// assertion encoded the defect #3630 was filed against: a <see cref="TaskCanceledException"/>
    /// wrapping a live transport fault is not a token-signalled shutdown, it is a connection that
    /// died on the wire, and calling it Terminal parked both Telegram polling loops for 3+ hours on
    /// 2026-08-28. Cooperative cancellation is still pinned Terminal by
    /// <see cref="Classify_CooperativeTaskCanceled_IsTerminal"/> and
    /// <see cref="Classify_CooperativeTaskCanceledWrappingNonTransientFault_IsTerminal"/>, neither
    /// of which this case may be satisfied by weakening.
    /// </summary>
    [Fact]
    public void Classify_CooperativeTaskCanceledWrappingTransportFault_IsTransient()
    {
        var ex = new TaskCanceledException(
            "A task was canceled.",
            new IOException("connection closed", new SocketException((int)SocketError.ConnectionReset)));

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Transient);
    }

    /// <summary>
    /// #3630 AC1 - the verbatim chain from the 2026-08-28 live trace, produced by a host
    /// sleep/resume killing every long-lived socket at once:
    /// TaskCanceledException -&gt; HttpRequestException (no status) -&gt; IOException -&gt;
    /// SocketException(10054, ConnectionReset). There is <b>no TimeoutException anywhere in this
    /// chain</b>, so the marker hunt the guard used to perform returned false and the whole
    /// classification fell to Terminal - never reaching the SocketException arm that already lists
    /// ConnectionReset as transient.
    /// </summary>
    [Fact]
    public void Classify_LiveHostResumeSocketResetChain_IsTransient()
    {
        var socket = new SocketException((int)SocketError.ConnectionReset); // 10054
        var io = new IOException(
            "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.",
            socket);
        var http = new HttpRequestException("An error occurred while sending the request.", io);
        var ex = new TaskCanceledException("The operation was canceled.", http);

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Transient);
    }

    /// <summary>
    /// #3630 AC2 - every socket error the classifier already names transient must reach the same
    /// verdict through the cancellation guard as it does through the direct walk. The guard and the
    /// <see cref="SocketException"/> arm disagreeing about the same socket error is precisely the
    /// defect; this pins that they cannot diverge again.
    /// </summary>
    [Theory]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.ConnectionAborted)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.TryAgain)]
    public void Classify_TaskCanceledWrappingTransientSocketError_IsTransient(SocketError error)
    {
        var ex = new TaskCanceledException(
            "The operation was canceled.",
            new HttpRequestException(
                "An error occurred while sending the request.",
                new IOException("transport", new SocketException((int)error))));

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Transient);
    }

    /// <summary>
    /// #3630 AC3 - the fail-closed half. A cancellation whose chain carries only a fault the
    /// classifier does <b>not</b> recognise as transient stays Terminal, so gateway shutdown is
    /// never retried. <see cref="SocketError.OperationAborted"/> (995) is deliberately outside the
    /// transient allow-list, which makes this the tightest possible counterpart to the AC1 tests
    /// above: identical shape, opposite verdict, decided solely by the socket error.
    /// </summary>
    [Fact]
    public void Classify_CooperativeTaskCanceledWrappingNonTransientFault_IsTerminal()
    {
        var ex = new TaskCanceledException(
            "A task was canceled.",
            new SocketException((int)SocketError.OperationAborted));

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Terminal);
    }

    /// <summary>
    /// #3630 AC3/AC6 - a cancellation wrapping an entirely unrecognised fault still fails closed.
    /// </summary>
    [Fact]
    public void Classify_CooperativeTaskCanceledWrappingUnknownFault_IsTerminal()
    {
        var ex = new TaskCanceledException("A task was canceled.", new FormatException("unrelated"));

        ChannelFailureClassifier.Classify(ex).ShouldBe(ChannelFailureKind.Terminal);
    }

    /// <summary>
    /// #3630 AC5 - the guard must inherit the SDK <c>IsTransient</c> convention too, so the Service
    /// Bus receive loop and the #2447 start path get the same verdict as Telegram from one place.
    /// </summary>
    [Fact]
    public void Classify_TaskCanceledWrappingTransientSdkFault_IsTransient()
        => ChannelFailureClassifier
            .Classify(new TaskCanceledException("A task was canceled.", new SelfDescribingException(isTransient: true)))
            .ShouldBe(ChannelFailureKind.Transient);

    /// <summary>
    /// #3630 AC2 - a retryable HTTP status surfaced beneath a cancellation is transient, matching
    /// the direct-walk verdict for the same status.
    /// </summary>
    [Fact]
    public void Classify_TaskCanceledWrappingServerErrorStatus_IsTransient()
        => ChannelFailureClassifier
            .Classify(new TaskCanceledException(
                "The operation was canceled.",
                new HttpRequestException("boom", null, HttpStatusCode.BadGateway)))
            .ShouldBe(ChannelFailureKind.Transient);

    /// <summary>
    /// #3630 AC3 - a deterministic 401 beneath a cancellation stays Terminal, so the guard does not
    /// become a blanket "anything nested is retryable" escape hatch.
    /// </summary>
    [Fact]
    public void Classify_TaskCanceledWrappingUnauthorizedStatus_IsTerminal()
        => ChannelFailureClassifier
            .Classify(new TaskCanceledException(
                "The operation was canceled.",
                new HttpRequestException("nope", null, HttpStatusCode.Unauthorized)))
            .ShouldBe(ChannelFailureKind.Terminal);

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
