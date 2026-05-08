using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class LocationRegistryConfigurationTests
{
    [Fact]
    public void PlatformConfig_DeserializesGatewayLocations()
    {
        const string json = """
                            {
                              "gateway": {
                                "locations": {
                                  "repo-root": {
                                    "type": "filesystem",
                                    "path": "Q:\\repos\\botnexus",
                                    "description": "Repository root"
                                  },
                                  "gateway-api": {
                                    "type": "api",
                                    "endpoint": "https://example.test"
                                  }
                                }
                              }
                            }
                            """;

        var config = JsonSerializer.Deserialize<PlatformConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        config.ShouldNotBeNull();
        config!.Gateway!.Locations.ShouldContainKey("repo-root");
        config.Gateway.Locations!["repo-root"].Path.ShouldBe("Q:\\repos\\botnexus");
        config.Gateway.Locations!["repo-root"].Description.ShouldBe("Repository root");
        config.Gateway.Locations!["gateway-api"].Endpoint.ShouldBe("https://example.test");
    }

    [Fact]
    public void PlatformConfigLoader_Validate_LocationsRequireTypeSpecificFields()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Locations = new Dictionary<string, LocationConfig>
                {
                    ["repo-root"] = new() { Type = "filesystem" },
                    ["gateway-api"] = new() { Type = "api" },
                    ["memory-db"] = new() { Type = "database" }
                }
            }
        };

        var errors = PlatformConfigLoader.Validate(config);

        errors.ShouldContain(error => error.Contains("gateway.locations.repo-root.path is required for filesystem locations.", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("gateway.locations.gateway-api.endpoint is required for api locations.", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("gateway.locations.memory-db.connectionString is required for database locations.", StringComparison.Ordinal));
    }
}
