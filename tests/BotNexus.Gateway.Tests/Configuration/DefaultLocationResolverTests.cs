using BotNexus.Domain.World;
using BotNexus.Gateway.Configuration;
using FluentAssertions;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class DefaultLocationResolverTests
{
    [Fact]
    public void Resolve_ReturnsNamedLocation_AndResolvePathSupportsFilesystem()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Locations = new Dictionary<string, LocationConfig>
                {
                    ["repo-root"] = new()
                    {
                        Type = "filesystem",
                        Path = "Q:\\repos\\botnexus"
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
        location.Should().NotBeNull();
        location!.Type.Should().Be(LocationType.FileSystem);
        resolver.ResolvePath("repo-root").Should().Be("Q:\\repos\\botnexus");
        resolver.ResolvePath("gateway-api").Should().BeNull();
        resolver.GetAll().Should().ContainSingle(x => x.Name == "gateway-api");
    }
}
