using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Satellites;

/// <summary>
/// Registry for tracking satellite connection status. Satellites register on connect
/// and are marked offline on disconnect. A background service detects stale connections.
/// </summary>
public interface ISatelliteRegistry
{
    /// <summary>Gets all registered satellites with current status.</summary>
    IReadOnlyList<SatelliteConnectionInfo> GetAll();

    /// <summary>Gets a satellite by ID, or null if not found.</summary>
    SatelliteConnectionInfo? GetById(string satelliteId);

    /// <summary>Gets all online satellites owned by a specific user.</summary>
    IReadOnlyList<SatelliteConnectionInfo> GetOnlineForUser(string userId);

    /// <summary>
    /// Marks a configured satellite as online with the given SignalR connection ID. A new connection
    /// atomically replaces any older connection for the same satellite.
    /// </summary>
    /// <returns><c>true</c> when the configured satellite was updated; otherwise <c>false</c>.</returns>
    bool MarkOnline(string satelliteId, string connectionId);

    /// <summary>
    /// Marks a satellite offline only when <paramref name="connectionId"/> is still its active
    /// connection. This prevents an older disconnect or stale snapshot from taking a replacement
    /// connection offline.
    /// </summary>
    /// <returns><c>true</c> when the active connection was taken offline; otherwise <c>false</c>.</returns>
    bool MarkOffline(string satelliteId, string connectionId);

    /// <summary>
    /// Records a heartbeat only when <paramref name="connectionId"/> is still the active connection.
    /// </summary>
    /// <returns><c>true</c> when the heartbeat was accepted; otherwise <c>false</c>.</returns>
    bool RecordHeartbeat(string satelliteId, string connectionId);

    /// <summary>
    /// Gets all online satellites whose last heartbeat is older than their stale timeout, measured on
    /// the registry's own monotonic clock. The caller does not supply an instant: a wall-clock "now"
    /// from a second clock is exactly the mixed-source comparison that #3780 removed.
    /// </summary>
    IReadOnlyList<SatelliteConnectionInfo> GetStaleSatellites();
}
