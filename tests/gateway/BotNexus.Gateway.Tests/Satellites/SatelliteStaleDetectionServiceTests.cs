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

        // Start service and poll for the outcome.
        //
        // #2825: this previously slept 200ms and hoped a 50ms cycle had run, which failed on a
        // loaded container (Expected Offline, Actual Online) because the background service had
        // not been scheduled yet. Sleeping a fixed time asserts the host's throughput; polling
        // for the condition asserts the behaviour. A service that never marks the satellite
        // stale still fails, so the assertion is unchanged - it just no longer requires the
        // scheduler to be prompt.
        var task = service.StartAsync(cts.Token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (registry.GetById("stale-sat")?.Status != SatelliteStatus.Offline
            && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

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
