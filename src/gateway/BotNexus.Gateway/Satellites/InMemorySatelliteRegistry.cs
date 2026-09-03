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

    /// <summary>
    /// Monotonic heartbeat stamps, keyed by satellite id. These are <see cref="TimeProvider.GetTimestamp"/>
    /// readings, NOT wall-clock instants: a host clock correction (NTP step, VM resume, container host
    /// time sync) cannot move them. Freshness is decided from these and only these, so that a backwards
    /// step cannot pin a dead satellite "Online" forever and a forward step cannot expire the whole fleet
    /// in one sweep (#3780). <see cref="SatelliteConnectionInfo.LastSeen"/> remains the wall-clock value
    /// and stays purely a display concern.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _lastSeenTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InMemorySatelliteRegistry> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a new registry, seeding from platform config satellites.</summary>
    /// <param name="platformConfig">Platform configuration supplying the configured satellites.</param>
    /// <param name="logger">Logger for lifecycle events.</param>
    /// <param name="timeProvider">
    /// Clock used for both the wall-clock <c>LastSeen</c> display stamp and the monotonic freshness
    /// stamp. Injectable so a test can drive staleness deterministically; defaults to
    /// <see cref="TimeProvider.System"/> for production registration.
    /// </param>
    public InMemorySatelliteRegistry(
        IOptionsMonitor<PlatformConfig> platformConfig,
        ILogger<InMemorySatelliteRegistry> logger,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        SeedFromConfig(platformConfig.CurrentValue);
    }

    /// <summary>
    /// Test constructor that accepts pre-built entries. Any entry that already carries a
    /// <see cref="SatelliteConnectionInfo.LastSeen"/> has its wall-clock age converted ONCE, here, into
    /// a monotonic baseline so a fixture can express "last seen five minutes ago" without the running
    /// registry ever comparing across two clocks.
    /// </summary>
    internal InMemorySatelliteRegistry(
        IEnumerable<SatelliteConnectionInfo> entries,
        ILogger<InMemorySatelliteRegistry> logger,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var nowWall = _timeProvider.GetUtcNow();
        var nowStamp = _timeProvider.GetTimestamp();
        var frequency = _timeProvider.TimestampFrequency;

        foreach (var entry in entries)
        {
            _satellites[entry.Id] = entry;
            if (entry.LastSeen.HasValue)
            {
                var age = nowWall - entry.LastSeen.Value;
                _lastSeenTimestamps[entry.Id] = nowStamp - (long)(age.TotalSeconds * frequency);
            }
        }
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
        info.LastSeen = _timeProvider.GetUtcNow();
        _lastSeenTimestamps[satelliteId] = _timeProvider.GetTimestamp();
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
        {
            info.LastSeen = _timeProvider.GetUtcNow();
            _lastSeenTimestamps[satelliteId] = _timeProvider.GetTimestamp();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SatelliteConnectionInfo> GetStaleSatellites() =>
        _satellites.Values
            .Where(s => s.Status == SatelliteStatus.Online && IsStale(s))
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// Decides freshness from two readings of the SAME monotonic clock. A satellite with no monotonic
    /// stamp has never been seen by this registry instance and is never reported stale.
    /// </summary>
    private bool IsStale(SatelliteConnectionInfo satellite)
    {
        if (!_lastSeenTimestamps.TryGetValue(satellite.Id, out var stamp))
            return false;

        return _timeProvider.GetElapsedTime(stamp).TotalSeconds > satellite.StaleTimeoutSeconds;
    }

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
