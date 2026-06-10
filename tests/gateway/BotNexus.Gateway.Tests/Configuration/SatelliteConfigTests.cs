using BotNexus.Domain.World;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class SatelliteConfigTests
{
    [Fact]
    public void WorldDescriptorBuilder_WithSatellites_ResolvesSatellitesFromConfig()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "test", Name = "Test" },
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["sat_desktop"] = new()
                    {
                        DisplayName = "Desktop",
                        Platform = "windows",
                        ApiKey = "sat_testkey123",
                        Capabilities = ["notify", "canvas"],
                        OwnerUserId = "jon",
                        Enabled = true
                    },
                    ["sat_laptop"] = new()
                    {
                        DisplayName = "Laptop",
                        Platform = "macos",
                        ApiKey = "sat_testkey456",
                        Capabilities = ["notify", "canvas", "exec"],
                        OwnerUserId = "jon",
                        Enabled = true
                    }
                }
            }
        };

        var world = WorldDescriptorBuilder.Build(config, null, null);

        world.Satellites.Count.ShouldBe(2);

        var desktop = world.Satellites.First(s => s.Id == "sat_desktop");
        desktop.DisplayName.ShouldBe("Desktop");
        desktop.Platform.ShouldBe(SatellitePlatform.Windows);
        desktop.OwnerUserId.ShouldBe("jon");
        desktop.Capabilities.Count.ShouldBe(2);
        desktop.Capabilities.ShouldContain(SatelliteCapability.Notify);
        desktop.Capabilities.ShouldContain(SatelliteCapability.Canvas);

        var laptop = world.Satellites.First(s => s.Id == "sat_laptop");
        laptop.Platform.ShouldBe(SatellitePlatform.MacOS);
        laptop.Capabilities.Count.ShouldBe(3);
        laptop.Capabilities.ShouldContain(SatelliteCapability.Exec);
    }

    [Fact]
    public void WorldDescriptorBuilder_DisabledSatellites_AreExcluded()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "test", Name = "Test" },
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["sat_active"] = new()
                    {
                        DisplayName = "Active",
                        Platform = "windows",
                        Capabilities = ["notify"],
                        OwnerUserId = "jon",
                        Enabled = true
                    },
                    ["sat_disabled"] = new()
                    {
                        DisplayName = "Disabled",
                        Platform = "linux",
                        Capabilities = ["notify"],
                        OwnerUserId = "jon",
                        Enabled = false
                    }
                }
            }
        };

        var world = WorldDescriptorBuilder.Build(config, null, null);

        world.Satellites.Count.ShouldBe(1);
        world.Satellites[0].Id.ShouldBe("sat_active");
    }

    [Fact]
    public void WorldDescriptorBuilder_SatelliteWithoutOwner_IsExcluded()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "test", Name = "Test" },
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["sat_orphan"] = new()
                    {
                        DisplayName = "Orphan",
                        Platform = "windows",
                        Capabilities = ["notify"],
                        OwnerUserId = null, // No owner
                        Enabled = true
                    }
                }
            }
        };

        var world = WorldDescriptorBuilder.Build(config, null, null);

        world.Satellites.ShouldBeEmpty();
    }

    [Fact]
    public void WorldDescriptorBuilder_InvalidCapabilities_AreSkipped()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "test", Name = "Test" },
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["sat_test"] = new()
                    {
                        DisplayName = "Test",
                        Platform = "windows",
                        Capabilities = ["notify", "unknown_cap", "canvas"],
                        OwnerUserId = "jon",
                        Enabled = true
                    }
                }
            }
        };

        var world = WorldDescriptorBuilder.Build(config, null, null);

        var sat = world.Satellites.ShouldHaveSingleItem();
        sat.Capabilities.Count.ShouldBe(2);
        sat.Capabilities.ShouldContain(SatelliteCapability.Notify);
        sat.Capabilities.ShouldContain(SatelliteCapability.Canvas);
    }

    [Fact]
    public void WorldDescriptorBuilder_UnknownPlatform_DefaultsToWindows()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "test", Name = "Test" },
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["sat_test"] = new()
                    {
                        DisplayName = "Test",
                        Platform = "freebsd",
                        Capabilities = ["notify"],
                        OwnerUserId = "jon",
                        Enabled = true
                    }
                }
            }
        };

        var world = WorldDescriptorBuilder.Build(config, null, null);

        world.Satellites.ShouldHaveSingleItem().Platform.ShouldBe(SatellitePlatform.Windows);
    }

    [Fact]
    public void WorldDescriptorBuilder_NoSatellitesConfig_ReturnsEmptyList()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "test", Name = "Test" }
            }
        };

        var world = WorldDescriptorBuilder.Build(config, null, null);

        world.Satellites.ShouldBeEmpty();
    }

    [Fact]
    public void WorldDescriptorBuilder_SatellitesAreSortedById()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "test", Name = "Test" },
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["sat_zulu"] = new() { Platform = "linux", Capabilities = ["notify"], OwnerUserId = "jon", Enabled = true },
                    ["sat_alpha"] = new() { Platform = "windows", Capabilities = ["notify"], OwnerUserId = "jon", Enabled = true },
                    ["sat_mike"] = new() { Platform = "macos", Capabilities = ["notify"], OwnerUserId = "jon", Enabled = true }
                }
            }
        };

        var world = WorldDescriptorBuilder.Build(config, null, null);

        world.Satellites.Count.ShouldBe(3);
        world.Satellites[0].Id.ShouldBe("sat_alpha");
        world.Satellites[1].Id.ShouldBe("sat_mike");
        world.Satellites[2].Id.ShouldBe("sat_zulu");
    }
}
