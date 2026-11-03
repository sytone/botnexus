using BotNexus.Satellite.Client.Core;
using BotNexus.Satellite.Client.SignalR;
namespace BotNexus.Satellite.Client.SignalR.Tests;
public sealed class RequestValidationTests
{
    [Theory]
    [InlineData("Authorization", "Bearer secret")]
    [InlineData("X-Satellite-Id", "caller-selected")]
    [InlineData("X-Api-Key", "bad\r\nheader")]
    [InlineData("X-Tunnel-Authorization", "Bearer tunnel-secret")]
    public void Request_UnvalidatedHeader_Rejected(string name, string value)
    {
        var headers = new Dictionary<string, string> { ["X-Api-Key"] = "gateway-secret" };
        headers[name] = value;
        var request = new ConnectionRequest { HubUri = new Uri("https://example.test/hub/satellite"), Headers = headers };
        using var wire = new HttpClientHandler();
        var error = Should.Throw<SatelliteConnectionException>(() => TransportSecurity.CreateHandler(request, wire));
        error.Failure.ShouldBe(ConnectionFailure.AccessDenied);
        error.ToString().ShouldNotContain(value);
    }
    [Theory]
    [InlineData("https://example.test/hub/satellite?key=secret")]
    [InlineData("https://example.test/hub/other")]
    [InlineData("http://example.test/hub/satellite")]
    public void Request_UnsafeHubUri_Rejected(string endpoint)
    {
        var request = new ConnectionRequest { HubUri = new Uri(endpoint), Headers = new Dictionary<string, string> { ["X-Api-Key"] = "gateway-secret" } };
        using var wire = new HttpClientHandler();
        Should.Throw<SatelliteConnectionException>(() => TransportSecurity.CreateHandler(request, wire)).Failure.ShouldBe(ConnectionFailure.AccessDenied);
    }
    [Fact]
    public void Handler_DefaultRedirectsAndCookies_Disabled()
    {
        var request = new ConnectionRequest { HubUri = new Uri("https://example.test/hub/satellite"), Headers = new Dictionary<string, string> { ["X-Api-Key"] = "gateway-secret" } };
        var wire = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = true, UseDefaultCredentials = true };
        using var guard = TransportSecurity.CreateHandler(request, wire);
        wire.AllowAutoRedirect.ShouldBeFalse();
        wire.UseCookies.ShouldBeFalse();
        wire.UseDefaultCredentials.ShouldBeFalse();
    }
}
