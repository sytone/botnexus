using BotNexus.Satellite.Client.SignalR;
namespace BotNexus.Satellite.Client.SignalR.Tests;
public sealed class ProfileTests
{
    [Fact]
    public void Profile_HeartbeatPreferences_ArePersistedAndApplied()
    {
        var json = "{\"GatewayEndpoint\":\"https://example.test\",\"SatelliteIdentifier\":\"sat-one\",\"HeartbeatIntervalSeconds\":10,\"StaleTimeoutSeconds\":60}";
        var profile = System.Text.Json.JsonSerializer.Deserialize<ClientProfile>(json) ?? throw new InvalidOperationException();
        profile.Validate();
        profile.ToOptions().HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(10));
        profile.ToOptions().StaleTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        System.Text.Json.JsonSerializer.Serialize(profile).ShouldContain("HeartbeatIntervalSeconds");
    }
    [Fact]
    public void Profile_HeartbeatPreferences_InvalidDeadlineRejects()
    {
        var json = "{\"GatewayEndpoint\":\"https://example.test\",\"SatelliteIdentifier\":\"sat-one\",\"HeartbeatIntervalSeconds\":60,\"StaleTimeoutSeconds\":60}";
        var profile = System.Text.Json.JsonSerializer.Deserialize<ClientProfile>(json) ?? throw new InvalidOperationException();
        Should.Throw<ArgumentException>(() => profile.Validate());
    }
    [Fact]
    public void CredentialScope_DifferentProfiles_SeparateTargets()
    {
        var first = new ClientProfile { GatewayEndpoint = "https://example.test", SatelliteIdentifier = "sat-one" };
        var second = new ClientProfile { GatewayEndpoint = "https://example.test", SatelliteIdentifier = "sat-two" };
        first.CredentialScope.ShouldNotBe(second.CredentialScope);
        new ClientProfile { GatewayEndpoint = "https://other.test", SatelliteIdentifier = "sat-one" }.CredentialScope.ShouldNotBe(first.CredentialScope);
        new ClientProfile { GatewayEndpoint = "https://EXAMPLE.test:443/", SatelliteIdentifier = "sat-one" }.CredentialScope.ShouldBe(first.CredentialScope);
    }
    [Fact]
    public void Profile_NonSecretJson_DoesNotContainCredentials()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ClientProfile { GatewayEndpoint = "https://example.test", SatelliteIdentifier = "sat-one" });
        json.ShouldNotContain("ApiKey");
        json.ShouldNotContain("Token");
        json.ShouldNotContain("CredentialScope");
    }
    [Theory]
    [InlineData("")]
    [InlineData("invalid/name")]
    [InlineData("identity\n")]
    public void Profile_InvalidIdentifier_Rejected(string identifier)
    {
        var profile = new ClientProfile { GatewayEndpoint = "https://example.test", SatelliteIdentifier = identifier };
        Should.Throw<ArgumentException>(() => profile.Validate());
    }
}
