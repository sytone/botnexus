using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Satellites;
using BotNexus.Gateway.Satellites;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Satellites;

public sealed class SatelliteStaleDetectionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_DetectsAndMarksStale()
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
                StaleTimeoutSeconds = 60
            }
        };

        var registry = new InMemorySatelliteRegistry(entries, NullLogger<InMemorySatelliteRegistry>.Instance);

        using var cts = new CancellationTokenSource();
        var service = new SatelliteStaleDetectionService(
            registry,
            NullLogger<SatelliteStaleDetectionService>.Instance,
            checkInterval: TimeSpan.FromMilliseconds(50));

        // Start service and let it run one cycle
        var task = service.StartAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        var sat = registry.GetById("stale-sat");
        Assert.NotNull(sat);
        Assert.Equal(SatelliteStatus.Offline, sat.Status);
    }

    [Fact]
    public async Task ExecuteAsync_FreshSatellites_NotMarkedStale()
    {
        var entries = new[]
        {
            new SatelliteConnectionInfo
            {
                Id = "fresh-sat",
                DisplayName = "Fresh",
                Platform = "windows",
                OwnerUserId = "jon",
                Status = SatelliteStatus.Online,
                LastSeen = DateTimeOffset.UtcNow,
                StaleTimeoutSeconds = 120
            }
        };

        var registry = new InMemorySatelliteRegistry(entries, NullLogger<InMemorySatelliteRegistry>.Instance);

        using var cts = new CancellationTokenSource();
        var service = new SatelliteStaleDetectionService(
            registry,
            NullLogger<SatelliteStaleDetectionService>.Instance,
            checkInterval: TimeSpan.FromMilliseconds(50));

        var task = service.StartAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

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
