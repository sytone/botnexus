using System.Net;
using System.Net.Sockets;
using System.Text;
using BotNexus.Agent.Providers.Core.Resilience;

namespace BotNexus.Agent.Providers.Core.Tests.Resilience;

public class TransientHttpRetryHandlerTests
{
    // ----- test inner handler: returns a scripted sequence of outcomes -----
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script;
        public int CallCount { get; private set; }
        public List<bool> ConnectionCloseSeen { get; } = new();
        public List<string?> BodiesSeen { get; } = new();

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
            => _script = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(steps);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            ConnectionCloseSeen.Add(request.Headers.ConnectionClose == true);
            BodiesSeen.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

            var step = _script.Count > 0 ? _script.Dequeue() : (_ => Respond(HttpStatusCode.OK));
            return step(request);
        }
    }

    private static HttpResponseMessage Respond(HttpStatusCode status) => new(status);

    private static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status)
        => _ => Respond(status);

    private static Func<HttpRequestMessage, HttpResponseMessage> Throw(Exception ex)
        => _ => throw ex;

    private static TransientHttpRetryHandler CreateHandler(ScriptedHandler inner, int maxRetries = 3)
        => new(logger: null, maxRetries: maxRetries, baseDelay: TimeSpan.FromMilliseconds(1))
        {
            InnerHandler = inner
        };

    private static HttpRequestMessage Post(string body = "{\"k\":1}")
        => new(HttpMethod.Post, "https://api.individual.githubcopilot.com/v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    // ----- #3035: bounded jitter on the transport-level backoff -----

    /// <summary>
    /// AC3 (deterministic bound) - pinned to <c>0</c> the handler must reproduce the historical linear
    /// 250/500/750ms ramp exactly, so adding jitter is provably not a schedule change.
    /// </summary>
    [Fact]
    public void ComputeDelay_RandomPinnedToZero_ReproducesTheHistoricalLinearRamp()
    {
        var handler = new TransientHttpRetryHandler(
            logger: null,
            baseDelay: TimeSpan.FromMilliseconds(250),
            randomSource: () => 0d);

        handler.ComputeDelay(0).TotalMilliseconds.ShouldBe(250);
        handler.ComputeDelay(1).TotalMilliseconds.ShouldBe(500);
        handler.ComputeDelay(2).TotalMilliseconds.ShouldBe(750);
    }

    /// <summary>
    /// AC3 (jittered bound) - pinned to the maximum, every delay is strictly longer than the un-jittered
    /// value and no more than <c>(1 + jitterFactor)</c> times it.
    /// </summary>
    [Theory]
    [InlineData(0, 250d)]
    [InlineData(1, 500d)]
    [InlineData(2, 750d)]
    public void ComputeDelay_RandomPinnedToMax_IsStrictlyLongerAndBoundedByTheJitterFactor(
        int attempt, double unJitteredMs)
    {
        var handler = new TransientHttpRetryHandler(
            logger: null,
            baseDelay: TimeSpan.FromMilliseconds(250),
            randomSource: () => 1d);

        var delay = handler.ComputeDelay(attempt).TotalMilliseconds;

        delay.ShouldBeGreaterThan(unJitteredMs);
        delay.ShouldBeLessThanOrEqualTo(unJitteredMs * (1 + RetryJitter.DefaultJitterFactor));
    }

    /// <summary>
    /// The default construction path must actually be randomised. This is the assertion that would fail if
    /// someone wired the seam but left the default source constant - the whole point of the change is that
    /// concurrent retriers draw <em>different</em> values.
    /// </summary>
    [Fact]
    public void ComputeDelay_DefaultRandomSource_ProducesVaryingDelaysAcrossDraws()
    {
        var handler = new TransientHttpRetryHandler(logger: null);

        var draws = Enumerable.Range(0, 64).Select(_ => handler.ComputeDelay(0).TotalMilliseconds).ToList();

        draws.Distinct().Count().ShouldBeGreaterThan(1, "the default jitter source must desynchronise retriers");
        draws.ShouldAllBe(d => d >= 250 && d <= 250 * (1 + RetryJitter.DefaultJitterFactor));
    }

    // ----- 421 retry behaviour (the motivating case) -----

    [Fact]
    public async Task Misdirected421_ThenSuccess_RetriesAndSucceeds()
    {
        var inner = new ScriptedHandler(
            Status(HttpStatusCode.MisdirectedRequest),
            Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        var response = await invoker.SendAsync(Post(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task Misdirected421_SetsConnectionClose_OnRetryOnly()
    {
        var inner = new ScriptedHandler(
            Status(HttpStatusCode.MisdirectedRequest),
            Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        await invoker.SendAsync(Post(), CancellationToken.None);

        // First attempt: no Connection: close. Retry attempt: Connection: close forces a fresh connection.
        Assert.False(inner.ConnectionCloseSeen[0]);
        Assert.True(inner.ConnectionCloseSeen[1]);
    }

    [Fact]
    public async Task Misdirected421_Persistent_ExhaustsRetriesAndReturnsLastResponse()
    {
        var inner = new ScriptedHandler(
            Status(HttpStatusCode.MisdirectedRequest),
            Status(HttpStatusCode.MisdirectedRequest),
            Status(HttpStatusCode.MisdirectedRequest),
            Status(HttpStatusCode.MisdirectedRequest));
        var invoker = new HttpMessageInvoker(CreateHandler(inner, maxRetries: 3));

        var response = await invoker.SendAsync(Post(), CancellationToken.None);

        // initial + 3 retries = 4 calls, last 421 returned (provider then surfaces it as before).
        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        Assert.Equal(4, inner.CallCount);
    }

    [Fact]
    public async Task Body_IsResentOnRetry()
    {
        var inner = new ScriptedHandler(
            Status(HttpStatusCode.MisdirectedRequest),
            Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        await invoker.SendAsync(Post("{\"payload\":\"hello\"}"), CancellationToken.None);

        Assert.Equal(2, inner.BodiesSeen.Count);
        Assert.Equal("{\"payload\":\"hello\"}", inner.BodiesSeen[0]);
        Assert.Equal("{\"payload\":\"hello\"}", inner.BodiesSeen[1]);
    }

    // ----- other transient statuses -----

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]    // 408
    [InlineData(HttpStatusCode.BadGateway)]        // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)] // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]    // 504
    public async Task TransientStatus_IsRetried(HttpStatusCode status)
    {
        var inner = new ScriptedHandler(Status(status), Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        var response = await invoker.SendAsync(Post(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    // ----- non-retriable statuses pass through untouched -----

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]    // 400
    [InlineData(HttpStatusCode.Unauthorized)]  // 401
    [InlineData(HttpStatusCode.Forbidden)]     // 403
    [InlineData(HttpStatusCode.NotFound)]      // 404
    public async Task NonRetriableStatus_IsNotRetried(HttpStatusCode status)
    {
        var inner = new ScriptedHandler(Status(status), Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        var response = await invoker.SendAsync(Post(), CancellationToken.None);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task TooManyRequests429_IsNotRetried_HandledUpstream()
    {
        // 429 carries server Retry-After semantics handled by ProviderHttpErrorHelper upstream.
        // This handler must not swallow that by blindly retrying.
        var inner = new ScriptedHandler(
            Status(HttpStatusCode.TooManyRequests),
            Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        var response = await invoker.SendAsync(Post(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, inner.CallCount);
    }

    // ----- transient transport exceptions -----

    [Fact]
    public async Task TransientSocketException_IsRetried()
    {
        var transient = new HttpRequestException(
            "connection reset", new SocketException((int)SocketError.ConnectionReset));
        var inner = new ScriptedHandler(Throw(transient), Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        var response = await invoker.SendAsync(Post(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task IoException_IsRetried()
    {
        var transient = new HttpRequestException("io error", new IOException("stream closed"));
        var inner = new ScriptedHandler(Throw(transient), Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        var response = await invoker.SendAsync(Post(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task NonTransientHttpRequestException_IsNotRetried_AndRethrown()
    {
        // A bare HttpRequestException with no transient inner cause (e.g. a hard DNS/name failure)
        // is not safe to retry blindly — surface it.
        var nonTransient = new HttpRequestException("name resolution failed");
        var inner = new ScriptedHandler(Throw(nonTransient), Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(Post(), CancellationToken.None));
        Assert.Equal(1, inner.CallCount);
    }

    // ----- cancellation -----

    [Fact]
    public async Task Cancellation_StopsRetrying()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedHandler(
            _ => { cts.Cancel(); return Respond(HttpStatusCode.MisdirectedRequest); },
            Status(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(CreateHandler(inner));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.SendAsync(Post(), cts.Token));
        // Only the first call happened; cancellation prevented the retry.
        Assert.Equal(1, inner.CallCount);
    }

    // ----- pure predicate coverage -----

    [Theory]
    [InlineData(HttpStatusCode.MisdirectedRequest, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void IsRetriableStatus_Classifies(HttpStatusCode status, bool expected)
        => Assert.Equal(expected, TransientHttpRetryHandler.IsRetriableStatus(status));

    [Theory]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.ConnectionAborted, true)]
    [InlineData(SocketError.TimedOut, true)]
    [InlineData(SocketError.HostUnreachable, true)]
    [InlineData(SocketError.NetworkUnreachable, true)]
    [InlineData(SocketError.TryAgain, true)]
    [InlineData(SocketError.AccessDenied, false)]
    public void IsTransientTransport_ClassifiesSocketErrors(SocketError code, bool expected)
    {
        var ex = new HttpRequestException("x", new SocketException((int)code));
        Assert.Equal(expected, TransientHttpRetryHandler.IsTransientTransport(ex));
    }

    [Fact]
    public void IsTransientTransport_IoException_IsTransient()
        => Assert.True(TransientHttpRetryHandler.IsTransientTransport(
            new HttpRequestException("x", new IOException("y"))));

    [Fact]
    public void IsTransientTransport_NoInnerCause_IsNotTransient()
        => Assert.False(TransientHttpRetryHandler.IsTransientTransport(
            new HttpRequestException("bare")));
}
