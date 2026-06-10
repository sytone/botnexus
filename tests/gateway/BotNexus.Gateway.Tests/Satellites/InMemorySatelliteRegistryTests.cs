using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Satellites;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Satellites;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BotNexus.Gateway.Tests.Satellites;

public sealed class InMemorySatelliteRegistryTests
{
    private static InMemorySatelliteRegistry CreateRegistry(
        Dictionary<string, SatelliteConfig>? satellites = null)
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Satellites = satellites
            }
        };
        var monitor = new TestOptionsMonitor<PlatformConfig>(config);
        return new InMemorySatelliteRegistry(monitor, NullLogger<InMemorySatelliteRegistry>.Instance);
    }

    [Fact]
    public void GetAll_EmptyConfig_ReturnsEmpty()
    {
        var registry = CreateRegistry();
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void GetAll_SeededFromConfig_ReturnsSatellites()
    {
        var registry = CreateRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["sat1"] = new() { DisplayName = "Desktop", Platform = "windows", OwnerUserId = "jon", ApiKey = "sat_key1" },
            ["sat2"] = new() { DisplayName = "Laptop", Platform = "macos", OwnerUserId = "jon", ApiKey = "sat_key2" }
        });

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Id == "sat1" && s.DisplayName == "Desktop");
        Assert.Contains(all, s => s.Id == "sat2" && s.DisplayName == "Laptop");
    }

    [Fact]
    public void GetAll_DisabledSatellite_Excluded()
    {
        var registry = CreateRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["enabled"] = new() { DisplayName = "Enabled", Platform = "windows", OwnerUserId = "jon", ApiKey = "k1", Enabled = true },
            ["disabled"] = new() { DisplayName = "Disabled", Platform = "linux", OwnerUserId = "jon", ApiKey = "k2", Enabled = false }
        });

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("enabled", all[0].Id);
    }

    [Fact]
    public void GetById_Existing_ReturnsSatellite()
    {
        var registry = CreateRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["sat1"] = new() { DisplayName = "Desktop", Platform = "windows", OwnerUserId = "jon", ApiKey = "k1" }
        });

        var result = registry.GetById("sat1");
        Assert.NotNull(result);
        Assert.Equal("sat1", result.Id);
    }

    [Fact]
    public void GetById_Unknown_ReturnsNull()
    {
        var registry = CreateRegistry();
        Assert.Null(registry.GetById("nonexistent"));
    }

    [Fact]
    public void MarkOnline_UpdatesStatusAndConnectionId()
    {
        var registry = CreateRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["sat1"] = new() { DisplayName = "Desktop", Platform = "windows", OwnerUserId = "jon", ApiKey = "k1" }
        });

        registry.MarkOnline("sat1", "conn-123");

        var sat = registry.GetById("sat1");
        Assert.NotNull(sat);
        Assert.Equal(SatelliteStatus.Online, sat.Status);
        Assert.Equal("conn-123", sat.ConnectionId);
        Assert.NotNull(sat.LastSeen);
    }

    [Fact]
    public void MarkOnline_UnknownSatellite_NoOp()
    {
        var registry = CreateRegistry();
        registry.MarkOnline("nonexistent", "conn-123"); // Should not throw
    }

    [Fact]
    public void MarkOffline_ClearsConnectionId()
    {
        var registry = CreateRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["sat1"] = new() { DisplayName = "Desktop", Platform = "windows", OwnerUserId = "jon", ApiKey = "k1" }
        });

        registry.MarkOnline("sat1", "conn-123");
        registry.MarkOffline("sat1");

        var sat = registry.GetById("sat1");
        Assert.NotNull(sat);
        Assert.Equal(SatelliteStatus.Offline, sat.Status);
        Assert.Null(sat.ConnectionId);
    }

    [Fact]
    public void RecordHeartbeat_UpdatesLastSeen()
    {
        var registry = CreateRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["sat1"] = new() { DisplayName = "Desktop", Platform = "windows", OwnerUserId = "jon", ApiKey = "k1" }
        });

        registry.MarkOnline("sat1", "conn-123");
        var firstSeen = registry.GetById("sat1")!.LastSeen;

        Thread.Sleep(10); // ensure time advances
        registry.RecordHeartbeat("sat1");

        var afterHeartbeat = registry.GetById("sat1")!.LastSeen;
        Assert.True(afterHeartbeat > firstSeen);
    }

    [Fact]
    public void GetOnlineForUser_FiltersCorrectly()
    {
        var registry = CreateRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["sat1"] = new() { DisplayName = "Jon Desktop", Platform = "windows", OwnerUserId = "jon", ApiKey = "k1" },
            ["sat2"] = new() { DisplayName = "Jon Laptop", Platform = "macos", OwnerUserId = "jon", ApiKey = "k2" },
            ["sat3"] = new() { DisplayName = "Other User", Platform = "linux", OwnerUserId = "alice", ApiKey = "k3" }
        });

        registry.MarkOnline("sat1", "c1");
        registry.MarkOnline("sat2", "c2");
        registry.MarkOnline("sat3", "c3");

        var jonSats = registry.GetOnlineForUser("jon");
        Assert.Equal(2, jonSats.Count);
        Assert.All(jonSats, s => Assert.Equal("jon", s.OwnerUserId));
    }

    [Fact]
    public void GetStaleSatellites_DetectsStale()
    {
        var entries = new[]
        {
            new SatelliteConnectionInfo
            {
                Id = "stale-sat",
                DisplayName = "Stale",
                Platform = "windows",
                OwnerUserId = "jon",
                Status = SatelliteStatus.Online,
                LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5),
                StaleTimeoutSeconds = 120
            },
            new SatelliteConnectionInfo
            {
                Id = "fresh-sat",
                DisplayName = "Fresh",
                Platform = "windows",
                OwnerUserId = "jon",
                Status = SatelliteStatus.Online,
                LastSeen = DateTimeOffset.UtcNow.AddSeconds(-10),
                StaleTimeoutSeconds = 120
            }
        };

        var registry = new InMemorySatelliteRegistry(entries, NullLogger<InMemorySatelliteRegistry>.Instance);
        var stale = registry.GetStaleSatellites(DateTimeOffset.UtcNow);

        Assert.Single(stale);
        Assert.Equal("stale-sat", stale[0].Id);
    }

    [Fact]
    public void GetStaleSatellites_OfflineSatellites_NotReported()
    {
        var entries = new[]
        {
            new SatelliteConnectionInfo
            {
                Id = "offline-sat",
                DisplayName = "Offline",
                Platform = "windows",
                OwnerUserId = "jon",
                Status = SatelliteStatus.Offline,
                LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10),
                StaleTimeoutSeconds = 120
            }
        };

        var registry = new InMemorySatelliteRegistry(entries, NullLogger<InMemorySatelliteRegistry>.Instance);
        var stale = registry.GetStaleSatellites(DateTimeOffset.UtcNow);
        Assert.Empty(stale);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
