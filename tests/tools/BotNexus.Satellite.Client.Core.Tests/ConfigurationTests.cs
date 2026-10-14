using BotNexus.Satellite.Client.Core;

namespace BotNexus.Satellite.Client.Core.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("https://gateway.example", false)]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://[::1]:5000", true)]
    public void Validate_SafeEndpoint_Accepts(string endpoint, bool localDevelopment)
    {
        var options = new SatelliteClientOptions { GatewayEndpoint = endpoint, AllowLoopbackHttp = localDevelopment };
        options.Validate().ShouldBeEmpty();
        options.GetHubUri().AbsolutePath.ShouldBe("/hub/satellite");
    }

    [Theory]
    [InlineData("http://gateway.example", true)]
    [InlineData("http://localhost:5000", false)]
    [InlineData("http://localhost.evil.example", true)]
    [InlineData("https://user:secret@gateway.example", false)]
    [InlineData("https://gateway.example?api_key=secret", false)]
    [InlineData("https://gateway.example#secret", false)]
    [InlineData("/relative", false)]
    [InlineData("file:///tmp/gateway", false)]
    [InlineData("", false)]
    public void Validate_UnsafeEndpoint_RejectsWithoutEchoingSecrets(string endpoint, bool localDevelopment)
    {
        var options = new SatelliteClientOptions { GatewayEndpoint = endpoint, AllowLoopbackHttp = localDevelopment };
        var errors = options.Validate();
        errors.ShouldNotBeEmpty();
        string.Join(" ", errors).ShouldNotContain("secret");
        Should.Throw<ArgumentException>(() => options.GetHubUri());
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(-1, 120)]
    [InlineData(120, 120)]
    [InlineData(121, 120)]
    [InlineData(30, 0)]
    public void Validate_HeartbeatNotBeforeStaleDeadline_Rejects(int heartbeat, int stale)
    {
        new SatelliteClientOptions
        {
            GatewayEndpoint = "https://gateway.example",
            HeartbeatInterval = TimeSpan.FromSeconds(heartbeat),
            StaleTimeout = TimeSpan.FromSeconds(stale)
        }.Validate().ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("http://demo.devtunnels.ms")]
    [InlineData("https://evil.example")]
    [InlineData("https://demo.devtunnels.ms.evil.example")]
    [InlineData("https://demo.devtunnels.ms/path")]
    [InlineData("https://demo.devtunnels.ms?token=secret")]
    [InlineData("https://user:secret@demo.devtunnels.ms")]
    [InlineData("https://demo.devtunnels.ms#secret")]
    [InlineData("https://other.devtunnels.ms")]
    public void Validate_TunnelBrowserOriginUnsafeOrDifferentFromGateway_Rejects(string origin)
    {
        new SatelliteClientOptions
        {
            GatewayEndpoint = "https://demo.devtunnels.ms",
            TunnelBrowserOrigin = origin
        }.Validate().ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_ConfiguredHttpsTunnelOrigin_Accepts()
    {
        new SatelliteClientOptions
        {
            GatewayEndpoint = "https://demo.devtunnels.ms",
            TunnelBrowserOrigin = "https://demo.devtunnels.ms"
        }.Validate().ShouldBeEmpty();
    }
}
