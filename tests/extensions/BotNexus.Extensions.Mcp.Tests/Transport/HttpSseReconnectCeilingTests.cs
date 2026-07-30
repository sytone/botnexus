using System.Net;
using System.Net.Http.Headers;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests.Transport;

/// <summary>
/// Regression tests for issue #2564: the SSE reconnect attempt counter was reset
/// unconditionally after every stream read, so a server returning
/// <c>200 text/event-stream</c> with an immediately-closed body reconnected forever.
/// </summary>
public sealed class HttpSseReconnectCeilingTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    /// <summary>AC1 + AC4: an instantly-closing server yields EXACTLY maxReconnectAttempts reconnects.</summary>
    [Fact]
    public async Task InstantlyClosingStream_StopsAtExactlyMaxReconnectAttempts()
    {
        var handler = new ScriptedSseHandler(_ => string.Empty);
        using var client = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(
            new Uri("http://localhost/mcp"),
            httpClient: client,
            maxReconnectAttempts: 3,
            reconnectBaseDelay: Tick,
            minReconnectDelay: Tick);

        await transport.ConnectAsync();
        var loop = transport.SseLoopTask;
        Assert.NotNull(loop);
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        // 1 initial GET from ConnectAsync + exactly 3 reconnect GETs.
        handler.CallCount.ShouldBe(4);
        await transport.DisposeAsync();
    }

    /// <summary>AC2: a connection that delivers at least one event resets the counter (healthy-server direction).</summary>
    [Fact]
    public async Task ConnectionDeliveringAnEvent_ResetsTheAttemptCounter()
    {
        const string Event = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n";

        // Call 1 = initial connect (empty). Call 2 = reconnect 1 (empty).
        // Call 3 = reconnect 2 delivers an event -> counter resets to 0.
        // Calls 4,5 = two more zero-progress reconnects -> ceiling of 2 reached, loop exits.
        var handler = new ScriptedSseHandler(n => n == 3 ? Event : string.Empty);
        using var client = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(
            new Uri("http://localhost/mcp"),
            httpClient: client,
            maxReconnectAttempts: 2,
            reconnectBaseDelay: Tick,
            minReconnectDelay: Tick);

        await transport.ConnectAsync();
        var loop = transport.SseLoopTask;
        Assert.NotNull(loop);
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        // Without the reset-on-progress the loop would have stopped after 3 calls.
        handler.CallCount.ShouldBe(5);

        // The delivered event really reached the response channel.
        var received = await transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        received.Id.ShouldNotBeNull();
        await transport.DisposeAsync();
    }

    /// <summary>AC3: consecutive zero-progress delays grow and each is >= the previous.</summary>
    [Fact]
    public void ConsecutiveZeroProgressDelays_GrowMonotonically()
    {
        var transport = new HttpSseMcpTransport(new Uri("http://localhost/mcp"));

        var delays = Enumerable.Range(1, 8).Select(transport.ComputeReconnectDelay).ToList();

        for (var i = 1; i < delays.Count; i++)
        {
            delays[i].ShouldBeGreaterThanOrEqualTo(delays[i - 1]);
        }

        delays[0].ShouldBe(TimeSpan.FromSeconds(1));
        delays[1].ShouldBe(TimeSpan.FromSeconds(2));
        delays[2].ShouldBe(TimeSpan.FromSeconds(4));
        delays[^1].ShouldBe(TimeSpan.FromSeconds(30));
    }

    /// <summary>AC3 floor: the configured minimum delay is applied even when the exponential term is smaller.</summary>
    [Fact]
    public void ReconnectDelay_NeverDropsBelowTheFloor()
    {
        var transport = new HttpSseMcpTransport(
            new Uri("http://localhost/mcp"),
            reconnectBaseDelay: TimeSpan.FromMilliseconds(1),
            minReconnectDelay: TimeSpan.FromMilliseconds(250));

        transport.ComputeReconnectDelay(1).ShouldBe(TimeSpan.FromMilliseconds(250));
        transport.ComputeReconnectDelay(2).ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Event counting is explicit: the parser reports how many events it delivered.</summary>
    [Fact]
    public async Task ParseSseStreamAsync_ReportsDeliveredEventCount()
    {
        var transport = new HttpSseMcpTransport(new Uri("http://localhost/mcp"));

        var empty = await transport.ParseSseStreamAsync(new StringReader(string.Empty), CancellationToken.None);
        empty.ShouldBe(0);

        var malformed = await transport.ParseSseStreamAsync(
            new StringReader("event: message\ndata: {bad-json\n\n"), CancellationToken.None);
        malformed.ShouldBe(0);

        var two = await transport.ParseSseStreamAsync(
            new StringReader(
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n" +
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}\n\n"),
            CancellationToken.None);
        two.ShouldBe(2);

        await transport.DisposeAsync();
    }

    /// <summary>Transport exceptions are still counted as zero-progress attempts and are not swallowed into a loop.</summary>
    [Fact]
    public async Task FailingReconnects_StillBoundedByCeiling()
    {
        var handler = new ScriptedSseHandler(n => n == 1 ? string.Empty : null);
        using var client = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(
            new Uri("http://localhost/mcp"),
            httpClient: client,
            maxReconnectAttempts: 3,
            reconnectBaseDelay: Tick,
            minReconnectDelay: Tick);

        await transport.ConnectAsync();
        var loop = transport.SseLoopTask;
        Assert.NotNull(loop);
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        handler.CallCount.ShouldBe(4);
        await transport.DisposeAsync();
    }

    /// <summary>
    /// Returns <c>200 text/event-stream</c> with the scripted body (null body =&gt; 500 error response).
    /// </summary>
    private sealed class ScriptedSseHandler(Func<int, string?> bodyForCall) : HttpMessageHandler
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _calls);
            var body = bodyForCall(n);

            if (body is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(string.Empty)
                });
            }

            var content = new StringContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
