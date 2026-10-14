using System.Net;
using System.Net.WebSockets;
using BotNexus.Satellite.Client.Core;
using BotNexus.Satellite.Client.SignalR;

namespace BotNexus.Satellite.Client.SignalR.Tests;

public sealed class TransportSecurityTests
{
    private static ConnectionRequest Request() => new()
    {
        HubUri = new Uri("https://example.devtunnels.ms/hub/satellite"),
        Headers = new Dictionary<string, string> { ["X-Api-Key"] = "gateway-secret", ["X-Tunnel-Authorization"] = "tunnel tunnel-secret" }
    };

    [Theory]
    [InlineData("https://other.test/hub/satellite/negotiate")]
    [InlineData("http://example.devtunnels.ms/hub/satellite/negotiate")]
    [InlineData("https://example.devtunnels.ms:444/hub/satellite/negotiate")]
    [InlineData("https://user@example.devtunnels.ms/hub/satellite/negotiate")]
    public async Task Http_CrossOrigin_DeniedBeforeSending(string target)
    {
        var wire = new Wire(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(TransportSecurity.CreateHandler(Request(), wire));
        var error = await Should.ThrowAsync<SatelliteConnectionException>(() => client.GetAsync(target));
        error.Failure.ShouldBe(ConnectionFailure.AccessDenied);
        wire.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(301, "application/json", ConnectionFailure.AuthenticationRequired)]
    [InlineData(302, "text/html", ConnectionFailure.AuthenticationRequired)]
    [InlineData(307, "application/json", ConnectionFailure.AuthenticationRequired)]
    [InlineData(308, "application/json", ConnectionFailure.AuthenticationRequired)]
    [InlineData(401, "application/json", ConnectionFailure.AuthenticationRequired)]
    [InlineData(403, "application/json", ConnectionFailure.AccessDenied)]
    [InlineData(200, "text/html", ConnectionFailure.AuthenticationRequired)]
    public async Task Http_AuthResponse_ClassifiedWithoutBody(int status, string mediaType, ConnectionFailure expected)
    {
        var wire = new Wire(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret login HTML", System.Text.Encoding.UTF8, mediaType) });
        using var client = new HttpClient(TransportSecurity.CreateHandler(Request(), wire));
        var error = await Should.ThrowAsync<SatelliteConnectionException>(() => client.GetAsync("https://example.devtunnels.ms/hub/satellite/negotiate"));
        error.Failure.ShouldBe(expected);
        error.ToString().ShouldNotContain("secret login HTML");
        wire.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Http_SameOrigin_PreservesSeparatedHeaders()
    {
        var wire = new Wire(message =>
        {
            message.Headers.GetValues("X-Api-Key").Single().ShouldBe("gateway-secret");
            message.Headers.GetValues("X-Tunnel-Authorization").Single().ShouldBe("tunnel tunnel-secret");
            message.Headers.Authorization.ShouldBeNull();
            message.RequestUri?.Query.ShouldBe("");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(TransportSecurity.CreateHandler(Request(), wire));
        using var response = await client.GetAsync("https://EXAMPLE.devtunnels.ms:443/hub/satellite/negotiate");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        wire.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("wss://other.test/hub/satellite")]
    [InlineData("ws://example.devtunnels.ms/hub/satellite")]
    [InlineData("wss://example.devtunnels.ms:444/hub/satellite")]
    [InlineData("wss://user@example.devtunnels.ms/hub/satellite")]
    public async Task WebSocket_CrossOrigin_DeniedBeforeConnector(string target)
    {
        var count = 0;
        var error = await Should.ThrowAsync<SatelliteConnectionException>(async () =>
        {
            using var socket = await TransportSecurity.ConnectWebSocketAsync(Request(), new Uri(target), (_, _, _) => { count++; throw new InvalidOperationException(); }, CancellationToken.None);
        });
        error.Failure.ShouldBe(ConnectionFailure.AccessDenied);
        count.ShouldBe(0);
    }

    [Fact]
    public async Task WebSocket_SameOrigin_ConnectorReceivesSeparatedHeaders()
    {
        var count = 0;
        using var socket = await TransportSecurity.ConnectWebSocketAsync(Request(), new Uri("wss://EXAMPLE.devtunnels.ms:443/hub/satellite?id=connection"), (uri, headers, _) =>
        {
            count++;
            uri.Scheme.ShouldBe("wss");
            headers["X-Api-Key"].ShouldBe("gateway-secret");
            headers["X-Tunnel-Authorization"].ShouldBe("tunnel tunnel-secret");
            headers.ContainsKey("Authorization").ShouldBeFalse();
            return ValueTask.FromResult<WebSocket>(new ClientWebSocket());
        }, CancellationToken.None);
        count.ShouldBe(1);
    }

    [Theory]
    [InlineData("https://other.test/hub/satellite")]
    [InlineData("https://example.devtunnels.ms:444/hub/satellite")]
    public async Task Connect_JsonNegotiationRedirect_DeniedBeforeSecondRequest(string target)
    {
        var wire = new Wire(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"url\":\"" + target + "\",\"accessToken\":\"server-secret\"}", System.Text.Encoding.UTF8, "application/json") });
        var sockets = 0;
        var transport = new SignalRSatelliteTransport(() => wire, (_, _, _) => { sockets++; throw new InvalidOperationException(); });
        var error = await Should.ThrowAsync<SatelliteConnectionException>(async () => { await using var connection = await transport.ConnectAsync(Request(), CancellationToken.None); });
        error.Failure.ShouldBe(ConnectionFailure.AccessDenied);
        wire.Count.ShouldBe(1);
        sockets.ShouldBe(0);
        error.ToString().ShouldNotContain("server-secret");
    }

    [Fact]
    public async Task Connect_Negotiation_HeadersReachWireAndCancellationHonored()
    {
        using var cancellation = new CancellationTokenSource();
        var wire = new Wire(message =>
        {
            message.Headers.GetValues("X-Api-Key").Single().ShouldBe("gateway-secret");
            message.Headers.GetValues("X-Tunnel-Authorization").Single().ShouldBe("tunnel tunnel-secret");
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var transport = new SignalRSatelliteTransport(() => wire);
        await Should.ThrowAsync<OperationCanceledException>(async () => { await using var connection = await transport.ConnectAsync(Request(), cancellation.Token); });
        wire.Count.ShouldBe(1);
    }

    private sealed class Wire(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(response(request));
        }
    }
}
