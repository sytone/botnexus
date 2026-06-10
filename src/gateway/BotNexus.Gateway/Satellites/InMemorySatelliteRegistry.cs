using System.Collections.Concurrent;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Satellites;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Satellites;

/// <summary>
/// In-memory satellite registry that tracks connection state. Initialized from
/// <see cref="GatewaySettingsConfig.Satellites"/> configuration on startup.
/// </summary>
public sealed class InMemorySatelliteRegistry : ISatelliteRegistry
{
    private readonly ConcurrentDictionary<string, SatelliteConnectionInfo> _satellites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InMemorySatelliteRegistry> _logger;

    /// <summary>Creates a new registry, seeding from platform config satellites.</summary>
    public InMemorySatelliteRegistry(
        IOptionsMonitor<PlatformConfig> platformConfig,
        ILogger<InMemorySatelliteRegistry> logger)
    {
        _logger = logger;
        SeedFromConfig(platformConfig.CurrentValue);
    }

    /// <summary>Test constructor that accepts pre-built entries.</summary>
    internal InMemorySatelliteRegistry(
        IEnumerable<SatelliteConnectionInfo> entries,
        ILogger<InMemorySatelliteRegistry> logger)
    {
        _logger = logger;
        foreach (var entry in entries)
            _satellites[entry.Id] = entry;
    }

    /// <inheritdoc />
    public IReadOnlyList<SatelliteConnectionInfo> GetAll() =>
        _satellites.Values.ToList().AsReadOnly();

    /// <inheritdoc />
    public SatelliteConnectionInfo? GetById(string satelliteId) =>
        _satellites.GetValueOrDefault(satelliteId);

    /// <inheritdoc />
    public IReadOnlyList<SatelliteConnectionInfo> GetOnlineForUser(string userId) =>
        _satellites.Values
            .Where(s => s.Status == SatelliteStatus.Online &&
                        string.Equals(s.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();

    /// <inheritdoc />
    public void MarkOnline(string satelliteId, string connectionId)
    {
        if (!_satellites.TryGetValue(satelliteId, out var info))
        {
            _logger.LogWarning("Attempted to mark unknown satellite {SatelliteId} as online", satelliteId);
            return;
        }

        info.Status = SatelliteStatus.Online;
        info.ConnectionId = connectionId;
        info.LastSeen = DateTimeOffset.UtcNow;
        _logger.LogInformation("Satellite {SatelliteId} connected (connection={ConnectionId})", satelliteId, connectionId);
    }

    /// <inheritdoc />
    public void MarkOffline(string satelliteId)
    {
        if (!_satellites.TryGetValue(satelliteId, out var info))
            return;

        info.Status = SatelliteStatus.Offline;
        info.ConnectionId = null;
        _logger.LogInformation("Satellite {SatelliteId} disconnected", satelliteId);
    }

    /// <inheritdoc />
    public void RecordHeartbeat(string satelliteId)
    {
        if (_satellites.TryGetValue(satelliteId, out var info))
            info.LastSeen = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public IReadOnlyList<SatelliteConnectionInfo> GetStaleSatellites(DateTimeOffset now) =>
        _satellites.Values
            .Where(s => s.Status == SatelliteStatus.Online &&
                        s.LastSeen.HasValue &&
                        (now - s.LastSeen.Value).TotalSeconds > s.StaleTimeoutSeconds)
            .ToList()
            .AsReadOnly();

    private void SeedFromConfig(PlatformConfig config)
    {
        var satellites = config.Gateway?.Satellites;
        if (satellites is null || satellites.Count == 0)
            return;

        foreach (var (id, satConfig) in satellites)
        {
            if (!satConfig.Enabled)
                continue;

            _satellites[id] = new SatelliteConnectionInfo
            {
                Id = id,
                DisplayName = satConfig.DisplayName ?? id,
                Platform = satConfig.Platform,
                OwnerUserId = satConfig.OwnerUserId ?? "unknown",
                Capabilities = satConfig.Capabilities ?? [],
                StaleTimeoutSeconds = satConfig.StaleTimeoutSeconds
            };
        }

        _logger.LogInformation("Satellite registry seeded with {Count} satellites from config", _satellites.Count);
    }
}
