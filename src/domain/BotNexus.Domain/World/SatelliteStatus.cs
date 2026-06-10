namespace BotNexus.Domain.World;

/// <summary>
/// Connection status of a satellite node.
/// </summary>
public enum SatelliteStatus
{
    /// <summary>Satellite is not connected to the gateway.</summary>
    Offline,

    /// <summary>Satellite has an active connection and is sending heartbeats.</summary>
    Online,

    /// <summary>Satellite has not sent a heartbeat within the configured timeout.</summary>
    Stale
}
