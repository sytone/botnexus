namespace BotNexus.Domain.World;

/// <summary>
/// Represents a satellite node — a lightweight persistent process running on a remote machine
/// that connects to the gateway for bidirectional interaction (notifications, canvas rendering,
/// and optionally remote command execution).
/// </summary>
public sealed record Satellite
{
    /// <summary>Unique satellite identifier (e.g., "sat_desktop_home").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name (e.g., "Jon's Desktop").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Platform the satellite runs on (windows, macos, linux).</summary>
    public required SatellitePlatform Platform { get; init; }

    /// <summary>User ID of the satellite's owner. Events are filtered to this user's conversations.</summary>
    public required string OwnerUserId { get; init; }

    /// <summary>Capabilities this satellite is allowed to perform.</summary>
    public IReadOnlyList<SatelliteCapability> Capabilities { get; init; } = [];

    /// <summary>Current connection status.</summary>
    public SatelliteStatus Status { get; init; } = SatelliteStatus.Offline;

    /// <summary>Last time the satellite sent a heartbeat or message.</summary>
    public DateTimeOffset? LastSeen { get; init; }
}
