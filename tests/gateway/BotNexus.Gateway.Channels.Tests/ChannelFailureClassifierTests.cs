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

    [Fact]
    public void Classify_UnknownException_FailsClosedAsTerminal()
        => ChannelFailureClassifier
            .Classify(new NotSupportedException())
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
