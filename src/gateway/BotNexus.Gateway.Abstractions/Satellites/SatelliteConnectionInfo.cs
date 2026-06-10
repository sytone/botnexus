using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Satellites;

/// <summary>
/// Runtime connection information for a satellite node, combining static config
/// with dynamic connection state.
/// </summary>
public sealed record SatelliteConnectionInfo
{
    /// <summary>Satellite identifier from config.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Platform the satellite runs on.</summary>
    public required string Platform { get; init; }

    /// <summary>Owner user ID. Events are filtered to this user's conversations.</summary>
    public required string OwnerUserId { get; init; }

    /// <summary>Capabilities this satellite supports.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Current connection status.</summary>
    public SatelliteStatus Status { get; set; } = SatelliteStatus.Offline;

    /// <summary>SignalR connection ID when online, null otherwise.</summary>
    public string? ConnectionId { get; set; }

    /// <summary>Last time a heartbeat or connect event was received.</summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>Configured stale timeout in seconds.</summary>
    public int StaleTimeoutSeconds { get; init; } = 120;
}
