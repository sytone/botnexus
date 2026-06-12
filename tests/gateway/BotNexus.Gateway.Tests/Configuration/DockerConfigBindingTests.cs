using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Verifies that PlatformConfig binding via IConfiguration does not crash when
/// host-level environment variables collide with property names (e.g. DOTNET_VERSION → "Version").
/// </summary>
public sealed class DockerConfigBindingTests
{
    [Fact]
    public void Bind_DoesNotCrash_WhenVersionKeyOverriddenByEnvironmentVariable()
    {
        // Arrange: simulate Docker where DOTNET_VERSION env var is prefix-stripped to "VERSION"
        // by the .NET host, overriding config.json's "version": 1 in the root IConfiguration.
        var configJson = """
        {
            "version": 1,
            "gateway": {
                "listenUrl": "http://0.0.0.0:5000"
            }
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(configJson)))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Simulates DOTNET_VERSION=10.0.9 after prefix stripping (last-wins, overrides JSON)
                ["VERSION"] = "10.0.9"
            })
            .Build();

        // The root "Version" key is "10.0.9" from the env var
        Assert.Equal("10.0.9", configuration["Version"]);

        // Act: bind should NOT throw — ConfigurationKeyName remaps Version to "_configVersion"
        // so the binder never attempts to parse "10.0.9" as int
        var config = new PlatformConfig();
        configuration.Bind(config);

        // Assert: Version stays at default (1) since "_configVersion" doesn't exist in config
        Assert.Equal(1, config.PlatformVersion);
        // Other properties still bind correctly from root
        Assert.Equal("http://0.0.0.0:5000", config.Gateway?.ListenUrl);
    }

    [Fact]
    public void Bind_WithoutCollision_StillBindsNormally()
    {
        // When there's no env var collision, binding works as before
        var configJson = """
        {
            "version": 1,
            "gateway": {
                "defaultAgentId": "test-agent"
            },
            "agents": {}
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(configJson)))
            .Build();

        var config = new PlatformConfig();
        configuration.Bind(config);

        // Version won't bind from IConfiguration (remapped key), but JSON deserialization
        // in PlatformConfigLoader.Load() and PostConfigure handles it.
        Assert.Equal("test-agent", config.Gateway?.DefaultAgentId);
    }
}
