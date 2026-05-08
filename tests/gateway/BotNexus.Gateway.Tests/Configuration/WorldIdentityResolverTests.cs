using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class WorldIdentityResolverTests
{
    [Fact]
    public void Resolve_WithConfiguredWorld_UsesConfiguredValues()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity
                {
                    Id = "local-dev",
                    Name = "Local Development",
                    Description = "Local development gateway",
                    Emoji = "🏠"
                }
            }
        };

        var world = WorldIdentityResolver.Resolve(config);

        world.Id.ShouldBe("local-dev");
        world.Name.ShouldBe("Local Development");
        world.Description.ShouldBe("Local development gateway");
        world.Emoji.ShouldBe("🏠");
    }

    [Fact]
    public void Resolve_WithoutConfiguredWorld_UsesDefaults()
    {
        var world = WorldIdentityResolver.Resolve(new PlatformConfig());

        world.Id.ShouldBe(Environment.MachineName);
        world.Name.ShouldBe("BotNexus Gateway");
    }
}
