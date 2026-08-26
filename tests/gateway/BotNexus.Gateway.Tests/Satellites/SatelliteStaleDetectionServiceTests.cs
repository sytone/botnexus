using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Satellites;
using BotNexus.Gateway.Satellites;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Satellites;

public sealed class SatelliteStaleDetectionServiceTests
{
    [Fact]
    public void DetectAndMarkStale_MarksExpiredSatelliteOffline()
    {
        var now = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
        var entries = new[]
        {
            new SatelliteConnectionInfo
            {
                Id = "stale-sat",
                DisplayName = "Stale",
                Platform = "windows",
                OwnerUserId = "jon",
                Status = SatelliteStatus.Online,
                LastSeen = now.AddMinutes(-5),
                StaleTimeoutSeconds = 60
            }
        };

        var registry = new InMemorySatelliteRegistry(entries, NullLogger<InMemorySatelliteRegistry>.Instance);

        var service = new SatelliteStaleDetectionService(
            registry,
            NullLogger<SatelliteStaleDetectionService>.Instance,
            timeProvider: new ManualTimeProvider(now));

        service.DetectAndMarkStale();

        var sat = registry.GetById("stale-sat");
        Assert.NotNull(sat);
        Assert.Equal(SatelliteStatus.Offline, sat.Status);
    }

    [Fact]
    public void DetectAndMarkStale_LeavesFreshSatelliteOnline()
    {
        var now = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
        var entries = new[]
        {
            new SatelliteConnectionInfo
            {
                Id = "fresh-sat",
                DisplayName = "Fresh",
                Platform = "windows",
                OwnerUserId = "jon",
                Status = SatelliteStatus.Online,
                LastSeen = now,
                StaleTimeoutSeconds = 120
            }
        };

        var registry = new InMemorySatelliteRegistry(entries, NullLogger<InMemorySatelliteRegistry>.Instance);

        var service = new SatelliteStaleDetectionService(
            registry,
            NullLogger<SatelliteStaleDetectionService>.Instance,
            timeProvider: new ManualTimeProvider(now));

        service.DetectAndMarkStale();

        var sat = registry.GetById("fresh-sat");
        Assert.NotNull(sat);
        Assert.Equal(SatelliteStatus.Online, sat.Status);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationStopsGracefully()
    {
        var entries = Array.Empty<SatelliteConnectionInfo>();
        var registry = new InMemorySatelliteRegistry(entries, NullLogger<InMemorySatelliteRegistry>.Instance);

        using var cts = new CancellationTokenSource();
        var service = new SatelliteStaleDetectionService(
            registry,
            NullLogger<SatelliteStaleDetectionService>.Instance,
            checkInterval: TimeSpan.FromSeconds(60));

        var task = service.StartAsync(cts.Token);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Should complete without throwing
    }
}
