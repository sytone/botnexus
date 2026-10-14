using System.Net;
using BotNexus.Satellite.Client.Core;
using BotNexus.Satellite.Client.SignalR;
namespace BotNexus.Satellite.Client.SignalR.Tests;
public sealed class UpgradeTests
{
    private static ConnectionRequest Request() => new() { HubUri = new Uri("https://example.test/hub/satellite"), Headers = new Dictionary<string, string> { ["X-Api-Key"] = "gateway-secret" } };
    [Theory]
    [InlineData(302, ConnectionFailure.AuthenticationRequired)]
    [InlineData(401, ConnectionFailure.AuthenticationRequired)]
    [InlineData(403, ConnectionFailure.AccessDenied)]
    [InlineData(200, ConnectionFailure.Transient)]
    public async Task Upgrade_UnexpectedResponse_Classified(int status, ConnectionFailure expected)
    {
        var wire = new Wire((HttpStatusCode)status);
        using var invoker = new HttpMessageInvoker(TransportSecurity.CreateWebSocketHandler(Request(), wire));
        using var message = new HttpRequestMessage(HttpMethod.Get, "wss://example.test/hub/satellite");
        var error = await Should.ThrowAsync<SatelliteConnectionException>(() => invoker.SendAsync(message, CancellationToken.None));
        error.Failure.ShouldBe(expected);
        wire.Count.ShouldBe(1);
    }
    [Fact]
    public async Task Upgrade_CrossOrigin_DeniedBeforeSending()
    {
        var wire = new Wire(HttpStatusCode.SwitchingProtocols);
        using var invoker = new HttpMessageInvoker(TransportSecurity.CreateWebSocketHandler(Request(), wire));
        using var message = new HttpRequestMessage(HttpMethod.Get, "wss://other.test/hub/satellite");
        var error = await Should.ThrowAsync<SatelliteConnectionException>(() => invoker.SendAsync(message, CancellationToken.None));
        error.Failure.ShouldBe(ConnectionFailure.AccessDenied);
        wire.Count.ShouldBe(0);
    }
    [Fact]
    public async Task Upgrade_Success_NoTunnelHeaderForDirectGateway()
    {
        var wire = new Wire(HttpStatusCode.SwitchingProtocols);
        using var invoker = new HttpMessageInvoker(TransportSecurity.CreateWebSocketHandler(Request(), wire));
        using var message = new HttpRequestMessage(HttpMethod.Get, "wss://example.test/hub/satellite");
        using var response = await invoker.SendAsync(message, CancellationToken.None);
        response.StatusCode.ShouldBe(HttpStatusCode.SwitchingProtocols);
        wire.Count.ShouldBe(1);
    }
    private sealed class Wire(HttpStatusCode status) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Count++;
            message.Headers.GetValues("X-Api-Key").Single().ShouldBe("gateway-secret");
            message.Headers.Contains("X-Tunnel-Authorization").ShouldBeFalse();
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
