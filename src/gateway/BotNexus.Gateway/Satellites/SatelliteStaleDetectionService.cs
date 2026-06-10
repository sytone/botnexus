using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Satellites;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Satellites;

/// <summary>
/// Background service that periodically checks for stale satellite connections
/// (no heartbeat within configured timeout) and marks them offline.
/// </summary>
public sealed class SatelliteStaleDetectionService : BackgroundService
{
    private readonly ISatelliteRegistry _registry;
    private readonly ILogger<SatelliteStaleDetectionService> _logger;
    private readonly TimeSpan _checkInterval;

    /// <summary>Creates the stale detection service with a 30-second check interval.</summary>
    public SatelliteStaleDetectionService(
        ISatelliteRegistry registry,
        ILogger<SatelliteStaleDetectionService> logger,
        TimeSpan? checkInterval = null)
    {
        _registry = registry;
        _logger = logger;
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Satellite stale detection service started (interval={Interval}s)", _checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                DetectAndMarkStale();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during satellite stale detection sweep");
            }
        }
    }

    private void DetectAndMarkStale()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = _registry.GetStaleSatellites(now);

        foreach (var satellite in stale)
        {
            _logger.LogWarning(
                "Satellite {SatelliteId} marked stale (last seen {LastSeen}, timeout={Timeout}s)",
                satellite.Id,
                satellite.LastSeen,
                satellite.StaleTimeoutSeconds);
            _registry.MarkOffline(satellite.Id);
        }
    }
}
