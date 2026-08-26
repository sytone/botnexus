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
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the periodic stale detector. The clock is injectable so a sweep can evaluate the
    /// configured heartbeat boundaries without depending on wall-clock timing.
    /// </summary>
    public SatelliteStaleDetectionService(
        ISatelliteRegistry registry,
        ILogger<SatelliteStaleDetectionService> logger,
        TimeSpan? checkInterval = null,
        TimeProvider? timeProvider = null)
    {
        _registry = registry;
        _logger = logger;
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    internal void DetectAndMarkStale()
    {
        var now = _timeProvider.GetUtcNow();
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
