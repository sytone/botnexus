using BotNexus.Domain.World;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class DefaultLocationResolverTests
{
    [Fact]
    public void Resolve_ReturnsNamedLocation_AndResolvePathSupportsFilesystem()
    {
        var testPath = Path.Combine(Path.GetTempPath(), "repos", "botnexus");
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Locations = new Dictionary<string, LocationConfig>
                {
                    ["repo-root"] = new()
                    {
                        Type = "filesystem",
                        Path = testPath
                    },
                    ["gateway-api"] = new()
                    {
                        Type = "api",
                        Endpoint = "https://example.test"
                    }
                }
            }
        };

        var resolver = new DefaultLocationResolver(config);

        var location = resolver.Resolve("repo-root");
        location.ShouldNotBeNull();
        location!.Type.ShouldBe(LocationType.FileSystem);
        resolver.ResolvePath("repo-root").ShouldBe(testPath);
        resolver.ResolvePath("gateway-api").ShouldBeNull();
        resolver.GetAll().Where(x => x.Name == "gateway-api").ShouldHaveSingleItem();
    }

    [Fact]
    public void Resolve_WithOptionsMonitor_RefreshesLocationsWithoutRestart()
    {
        var initialPath = Path.Combine(Path.GetTempPath(), "repos", "botnexus-a");
        var updatedPath = Path.Combine(Path.GetTempPath(), "repos", "botnexus-b");
        var monitor = new global::TestOptionsMonitor<PlatformConfig>(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Locations = new Dictionary<string, LocationConfig>
                {
                    ["repo-root"] = new() { Type = "filesystem", Path = initialPath }
                }
            }
        });

        var resolver = new DefaultLocationResolver(monitor);
        resolver.ResolvePath("repo-root").ShouldBe(initialPath);

        monitor.RaiseChanged(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Locations = new Dictionary<string, LocationConfig>
                {
                    ["repo-root"] = new() { Type = "filesystem", Path = updatedPath }
                }
            }
        });

        resolver.ResolvePath("repo-root").ShouldBe(updatedPath);
    }
}
