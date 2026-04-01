namespace BotNexus.Core.Abstractions;

/// <summary>Contract for the heartbeat service.</summary>
public interface IHeartbeatService
{
    /// <summary>Records the last heartbeat time.</summary>
    void Beat();

    /// <summary>Gets the time of the last heartbeat.</summary>
    DateTimeOffset? LastBeat { get; }

    /// <summary>Whether the service is currently healthy (received a beat recently).</summary>
    bool IsHealthy { get; }
}
