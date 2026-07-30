using System.Net;
using System.Text;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests.Transport;

/// <summary>
/// Covers the SSE reconnect ceiling (issue #2564). The attempt counter must only be reset on
/// EVIDENCE OF PROGRESS - at least one SSE event delivered on that connection - otherwise a server
/// that answers the GET with <c>200 text/event-stream</c> and an immediately-closed body resets the
/// counter on every read and drives an unbounded reconnect loop.
/// </summary>
public sealed class HttpSseReconnectCeilingTests
{
    private static readonly Uri Endpoint = new("http://localhost/mcp");

    private const string OneEvent = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n";

    [Fact]
    public async Task EmptyStream_StopsAfterExactlyMaxReconnectAttempts()
    {
        var handler = new SseScriptHandler(_ => string.Empty);
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(Endpoint, httpClient: http, maxReconnectAttempts: 3)
        {
            DelayAsync = static (_, _) => Task.CompletedTask,
        };

        await transport.ConnectAsync();
        await WaitForLoopAsync(transport);

        // 1 initial connect GET + at most 3 reconnect GETs. EXACT count, not "it terminated".
        handler.GetCount.ShouldBe(4);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task ProgressOnConnection_ResetsAttemptCounter()
    {
        // GET #0 (connect): empty. GETs #1..#5: one real SSE event each -> each must reset the
        // counter. GET #6 onwards: empty -> the ceiling finally applies.
        var handler = new SseScriptHandler(i => i >= 1 && i <= 5 ? OneEvent : string.Empty);
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(Endpoint, httpClient: http, maxReconnectAttempts: 3)
        {
            DelayAsync = static (_, _) => Task.CompletedTask,
        };

        await transport.ConnectAsync();
        await WaitForLoopAsync(transport);

        // 1 connect + 5 progress reconnects (counter reset each time) + 3 zero-progress reconnects.
        // Without the reset the loop would stop at 4; with an unconditional reset it never stops.
        handler.GetCount.ShouldBe(9);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task ZeroProgressReconnects_UseNonDecreasingExponentialBackoffWithFloor()
    {
        var delays = new List<TimeSpan>();
        var handler = new SseScriptHandler(_ => string.Empty);
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(Endpoint, httpClient: http, maxReconnectAttempts: 4)
        {
            DelayAsync = (d, _) =>
            {
                lock (delays) delays.Add(d);
                return Task.CompletedTask;
            },
        };

        await transport.ConnectAsync();
        await WaitForLoopAsync(transport);

        TimeSpan[] observed;
        lock (delays) observed = [.. delays];

        observed.Length.ShouldBe(4);

        // Floor: a rapidly-closing server can never be polled faster than 1 Hz.
        foreach (var d in observed)
        {
            d.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        }

        // Growth: each consecutive zero-progress delay is >= the previous one, and strictly grows.
        for (var i = 1; i < observed.Length; i++)
        {
            observed[i].ShouldBeGreaterThanOrEqualTo(observed[i - 1]);
            observed[i].ShouldBe(observed[i - 1] * 2);
        }

        await transport.DisposeAsync();
    }

    private static async Task WaitForLoopAsync(HttpSseMcpTransport transport)
    {
        var loop = transport.SseLoopTask;
        if (loop is null)
        {
            throw new InvalidOperationException("No SSE read loop was started.");
        }

        var completed = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(10)));
        ReferenceEquals(completed, loop)
            .ShouldBeTrue("SSE read loop did not terminate - the reconnect ceiling is inert.");
        await loop;
    }

    /// <summary>Serves a scripted SSE body per GET request index and counts the GETs.</summary>
    private sealed class SseScriptHandler(Func<int, string> bodyForIndex) : HttpMessageHandler
    {
        private int _getCount;

        public int GetCount => Volatile.Read(ref _getCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }

            var index = Interlocked.Increment(ref _getCount) - 1;
            var body = bodyForIndex(index);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
