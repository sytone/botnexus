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

    /// <summary>
    /// Capabilities this satellite advertises, copied verbatim from operator config
    /// (<c>gateway.satellites.{id}.capabilities</c>).
    ///
    /// <para><b>DISPLAY-ONLY - this is NOT an authorization control (#2606).</b> Nothing in the
    /// gateway reads this list to permit or refuse an operation. <see cref="ISatelliteRegistry"/>
    /// exposes only connection-state operations (register / heartbeat / stale sweep); there is no
    /// dispatch surface over which a satellite is asked to perform work, so there is nothing for a
    /// capability to gate. The list exists to be rendered by <c>botnexus satellite list</c> and by
    /// <c>GET /api/satellites</c>.</para>
    ///
    /// <para><b>Empty list means "no capabilities declared", not "nothing permitted" and not
    /// "everything permitted"</b> - because no permission decision is made from this field at all.
    /// An empty list and a fully populated list produce identical behaviour today.</para>
    ///
    /// <para>If a dispatch surface is ever added, this doc MUST be replaced by a single shared
    /// authorizer that refuses undeclared operations and logs the refusal with the satellite id and
    /// the requested capability. <c>SatelliteCapabilityEnforcementFenceTests</c> fails the build if a
    /// new consumer of this field appears, so the gap cannot be inherited silently.</para>
    /// </summary>
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
