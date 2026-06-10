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

    /// <summary>Marks a satellite as online with the given SignalR connection ID.</summary>
    void MarkOnline(string satelliteId, string connectionId);

    /// <summary>Marks a satellite as offline (disconnect or stale timeout).</summary>
    void MarkOffline(string satelliteId);

    /// <summary>Records a heartbeat from the satellite, updating LastSeen.</summary>
    void RecordHeartbeat(string satelliteId);

    /// <summary>Gets all satellites that have not sent a heartbeat within their stale timeout.</summary>
    IReadOnlyList<SatelliteConnectionInfo> GetStaleSatellites(DateTimeOffset now);
}
