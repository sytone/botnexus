using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests.Transport;

/// <summary>
/// Covers MCP Streamable HTTP session-expiry recovery: a 404 on a request carrying a stale
/// Mcp-Session-Id must clear the session, re-initialize, and replay the original request once.
/// </summary>
public sealed class HttpSseSessionExpiryTests
{
    private const string SessionHeader = "Mcp-Session-Id";
    private static readonly Uri Endpoint = new("http://localhost/mcp");

    [Fact]
    public async Task ExpiredSession404_ReInitializes_AndReplaysOriginalRequest()
    {
        var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(Endpoint, httpClient: http, maxReconnectAttempts: 0);

        // Establish "old-session" via the GET connect handshake.
        await transport.ConnectAsync();
        transport.SessionId.ShouldBe("old-session");

        var toolCall = new JsonRpcRequest { Id = 7, Method = "tools/call" };
        await transport.SendAsync(toolCall);

        // OBSERVABLE 1: the caller got the reply to its ORIGINAL request, not the initialize result.
        var response = await transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        response.Id.ShouldNotBeNull();
        JsonSerializer.Serialize(response.Id).ShouldBe("7");
        response.Result!.Value.GetProperty("replayed").GetBoolean().ShouldBeTrue();

        // OBSERVABLE 2: exact ordered wire sequence.
        handler.Log.Count.ShouldBe(5);

        handler.Log[0].Method.ShouldBe("GET");

        // Original tools/call carrying the stale session -> 404.
        handler.Log[1].Method.ShouldBe("POST");
        handler.Log[1].SessionId.ShouldBe("old-session");
        handler.Log[1].Body.ShouldContain("tools/call");

        // Re-initialize MUST be sent WITHOUT a session id.
        handler.Log[2].Method.ShouldBe("POST");
        handler.Log[2].Body.ShouldContain("\"method\":\"initialize\"");
        handler.Log[2].SessionId.ShouldBeNull();

        // notifications/initialized on the NEW session.
        handler.Log[3].Body.ShouldContain("notifications/initialized");
        handler.Log[3].SessionId.ShouldBe("new-session");

        // OBSERVABLE 3: the original request was actually replayed, on the NEW session.
        handler.Log[4].Method.ShouldBe("POST");
        handler.Log[4].Body.ShouldContain("tools/call");
        handler.Log[4].SessionId.ShouldBe("new-session");

        transport.SessionId.ShouldBe("new-session");

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Persistent404_DoesNotLoop_AndSurfacesError()
    {
        var handler = new Always404AfterInitHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(Endpoint, httpClient: http, maxReconnectAttempts: 0);

        await transport.ConnectAsync();
        transport.SessionId.ShouldBe("old-session");

        var act = () => transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "tools/call" });
        await act.ShouldThrowAsync<HttpRequestException>();

        // GET, POST(404), initialize, notifications/initialized, replay POST(404). No further attempts.
        handler.Log.Count(r => r.Body.Contains("tools/call")).ShouldBe(2);
        handler.Log.Count(r => r.Body.Contains("\"method\":\"initialize\"")).ShouldBe(1);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task NonSession404_DoesNotTriggerReInitialize()
    {
        var handler = new NoSessionHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(Endpoint, httpClient: http, maxReconnectAttempts: 0);

        await transport.ConnectAsync();
        transport.SessionId.ShouldBeNull();

        var act = () => transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "tools/call" });
        await act.ShouldThrowAsync<HttpRequestException>();

        handler.Log.Count(r => r.Body.Contains("\"method\":\"initialize\"")).ShouldBe(0);
        handler.Log.Count(r => r.Body.Contains("tools/call")).ShouldBe(1);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task NonNotFoundFailure_KeepsExistingBehaviour_NoReInitialize()
    {
        var handler = new StatusAfterConnectHandler(HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(Endpoint, httpClient: http, maxReconnectAttempts: 0);

        await transport.ConnectAsync();

        var act = () => transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "tools/call" });
        await act.ShouldThrowAsync<HttpRequestException>();

        handler.Log.Count(r => r.Body.Contains("\"method\":\"initialize\"")).ShouldBe(0);
        handler.Log.Count(r => r.Body.Contains("tools/call")).ShouldBe(1);
        transport.SessionId.ShouldBe("old-session");

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentExpiredRequests_ReInitializeExactlyOnce()
    {
        var handler = new ConcurrentScriptedHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(Endpoint, httpClient: http, maxReconnectAttempts: 0);

        await transport.ConnectAsync();

        await Task.WhenAll(
            transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "tools/call" }),
            transport.SendAsync(new JsonRpcRequest { Id = 2, Method = "tools/list" }));

        handler.Log.Count(r => r.Body.Contains("\"method\":\"initialize\"")).ShouldBe(1);
        transport.SessionId.ShouldBe("new-session");

        await transport.DisposeAsync();
    }

    // ---- helpers ----

    private sealed record Recorded(string Method, string? SessionId, string Body);

    private static HttpResponseMessage Json(string body, string? sessionId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (sessionId is not null)
        {
            response.Headers.TryAddWithoutValidation(SessionHeader, sessionId);
        }

        return response;
    }

    private abstract class RecordingHandler : HttpMessageHandler
    {
        public List<Recorded> Log { get; } = [];
        private readonly object _gate = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sessionId = request.Headers.TryGetValues(SessionHeader, out var v) ? v.FirstOrDefault() : null;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var record = new Recorded(request.Method.Method, sessionId, body);
            int index;
            lock (_gate)
            {
                Log.Add(record);
                index = Log.Count - 1;
            }

            return Respond(record, index);
        }

        protected abstract HttpResponseMessage Respond(Recorded request, int index);
    }

    /// <summary>GET -> old-session; first tools/call 404s; initialize -> new-session; replay -> 200.</summary>
    private sealed class ScriptedHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(Recorded request, int index)
        {
            if (request.Method == "GET")
            {
                var sse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
                };
                sse.Headers.TryAddWithoutValidation(SessionHeader, "old-session");
                return sse;
            }

            if (request.Body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
            {
                return Json("""{"jsonrpc":"2.0","id":-1,"result":{"capabilities":{}}}""", "new-session");
            }

            if (request.Body.Contains("notifications/initialized", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(string.Empty) };
            }

            // tools/call
            if (request.SessionId == "old-session")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("session expired") };
            }

            return Json("""{"jsonrpc":"2.0","id":7,"result":{"replayed":true}}""");
        }
    }

    /// <summary>Server keeps 404ing tools/call even after a successful re-initialize.</summary>
    private sealed class Always404AfterInitHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(Recorded request, int index)
        {
            if (request.Method == "GET")
            {
                var sse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
                };
                sse.Headers.TryAddWithoutValidation(SessionHeader, "old-session");
                return sse;
            }

            if (request.Body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
            {
                return Json("""{"jsonrpc":"2.0","id":-1,"result":{}}""", "new-session");
            }

            if (request.Body.Contains("notifications/initialized", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(string.Empty) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("gone") };
        }
    }

    /// <summary>Server never issues a session id; a 404 is therefore NOT a session expiry.</summary>
    private sealed class NoSessionHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(Recorded request, int index)
        {
            if (request.Method == "GET")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no such route") };
        }
    }

    private sealed class StatusAfterConnectHandler(HttpStatusCode status) : RecordingHandler
    {
        protected override HttpResponseMessage Respond(Recorded request, int index)
        {
            if (request.Method == "GET")
            {
                var sse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
                };
                sse.Headers.TryAddWithoutValidation(SessionHeader, "old-session");
                return sse;
            }

            return new HttpResponseMessage(status) { Content = new StringContent("boom") };
        }
    }

    /// <summary>Both concurrent requests 404 on the stale session; only one initialize must go out.</summary>
    private sealed class ConcurrentScriptedHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(Recorded request, int index)
        {
            if (request.Method == "GET")
            {
                var sse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
                };
                sse.Headers.TryAddWithoutValidation(SessionHeader, "old-session");
                return sse;
            }

            if (request.Body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
            {
                Thread.Sleep(50);
                return Json("""{"jsonrpc":"2.0","id":-1,"result":{}}""", "new-session");
            }

            if (request.Body.Contains("notifications/initialized", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(string.Empty) };
            }

            if (request.SessionId == "old-session")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("session expired") };
            }

            return Json("""{"jsonrpc":"2.0","id":99,"result":{}}""");
        }
    }
}
