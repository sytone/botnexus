using System.Text.Json;
using BotNexus.Domain.World;

namespace BotNexus.Domain.Tests;

public sealed class SatelliteTests
{
    [Fact]
    public void Satellite_Defaults_StatusIsOfflineAndCapabilitiesEmpty()
    {
        var satellite = new Satellite
        {
            Id = "sat_desktop_home",
            DisplayName = "Jon's Desktop",
            Platform = SatellitePlatform.Windows,
            OwnerUserId = "jon"
        };

        satellite.Status.ShouldBe(SatelliteStatus.Offline);
        satellite.Capabilities.ShouldBeEmpty();
        satellite.LastSeen.ShouldBeNull();
    }

    [Fact]
    public void Satellite_WithAllProperties_PreservesValues()
    {
        var lastSeen = DateTimeOffset.UtcNow;
        var satellite = new Satellite
        {
            Id = "sat_laptop_work",
            DisplayName = "Work Laptop",
            Platform = SatellitePlatform.MacOS,
            OwnerUserId = "jon",
            Capabilities = [SatelliteCapability.Notify, SatelliteCapability.Canvas, SatelliteCapability.Exec],
            Status = SatelliteStatus.Online,
            LastSeen = lastSeen
        };

        satellite.Id.ShouldBe("sat_laptop_work");
        satellite.DisplayName.ShouldBe("Work Laptop");
        satellite.Platform.ShouldBe(SatellitePlatform.MacOS);
        satellite.OwnerUserId.ShouldBe("jon");
        satellite.Capabilities.Count.ShouldBe(3);
        satellite.Capabilities.ShouldContain(SatelliteCapability.Notify);
        satellite.Capabilities.ShouldContain(SatelliteCapability.Canvas);
        satellite.Capabilities.ShouldContain(SatelliteCapability.Exec);
        satellite.Status.ShouldBe(SatelliteStatus.Online);
        satellite.LastSeen.ShouldBe(lastSeen);
    }

    [Fact]
    public void Satellite_JsonRoundTrip_PreservesAllValues()
    {
        var original = new Satellite
        {
            Id = "sat_desktop",
            DisplayName = "Desktop",
            Platform = SatellitePlatform.Linux,
            OwnerUserId = "admin",
            Capabilities = [SatelliteCapability.Notify, SatelliteCapability.Canvas],
            Status = SatelliteStatus.Stale,
            LastSeen = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero)
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<Satellite>(json);

        deserialized.ShouldNotBeNull();
        deserialized!.Id.ShouldBe("sat_desktop");
        deserialized.DisplayName.ShouldBe("Desktop");
        deserialized.Platform.ShouldBe(SatellitePlatform.Linux);
        deserialized.OwnerUserId.ShouldBe("admin");
        deserialized.Capabilities.Count.ShouldBe(2);
        deserialized.Status.ShouldBe(SatelliteStatus.Stale);
        deserialized.LastSeen.ShouldNotBeNull();
    }

    [Fact]
    public void SatellitePlatform_EnumValues_MatchExpected()
    {
        Enum.GetValues<SatellitePlatform>().Length.ShouldBe(3);
        ((int)SatellitePlatform.Windows).ShouldBe(0);
        ((int)SatellitePlatform.MacOS).ShouldBe(1);
        ((int)SatellitePlatform.Linux).ShouldBe(2);
    }

    [Fact]
    public void SatelliteCapability_EnumValues_MatchExpected()
    {
        Enum.GetValues<SatelliteCapability>().Length.ShouldBe(3);
        ((int)SatelliteCapability.Notify).ShouldBe(0);
        ((int)SatelliteCapability.Canvas).ShouldBe(1);
        ((int)SatelliteCapability.Exec).ShouldBe(2);
    }

    [Fact]
    public void SatelliteStatus_EnumValues_MatchExpected()
    {
        Enum.GetValues<SatelliteStatus>().Length.ShouldBe(3);
        ((int)SatelliteStatus.Offline).ShouldBe(0);
        ((int)SatelliteStatus.Online).ShouldBe(1);
        ((int)SatelliteStatus.Stale).ShouldBe(2);
    }

    [Fact]
    public void WorldDescriptor_WithSatellites_PreservesInRoundTrip()
    {
        var descriptor = new WorldDescriptor
        {
            Identity = new WorldIdentity
            {
                Id = "test-world",
                Name = "Test World"
            },
            Satellites =
            [
                new Satellite
                {
                    Id = "sat_one",
                    DisplayName = "Satellite One",
                    Platform = SatellitePlatform.Windows,
                    OwnerUserId = "user1",
                    Capabilities = [SatelliteCapability.Notify]
                },
                new Satellite
                {
                    Id = "sat_two",
                    DisplayName = "Satellite Two",
                    Platform = SatellitePlatform.MacOS,
                    OwnerUserId = "user2",
                    Capabilities = [SatelliteCapability.Canvas, SatelliteCapability.Exec]
                }
            ]
        };

        var json = JsonSerializer.Serialize(descriptor);
        var deserialized = JsonSerializer.Deserialize<WorldDescriptor>(json);

        deserialized.ShouldNotBeNull();
        deserialized!.Satellites.Count.ShouldBe(2);
        deserialized.Satellites[0].Id.ShouldBe("sat_one");
        deserialized.Satellites[0].Platform.ShouldBe(SatellitePlatform.Windows);
        deserialized.Satellites[1].Id.ShouldBe("sat_two");
        deserialized.Satellites[1].Capabilities.ShouldContain(SatelliteCapability.Exec);
    }
}
