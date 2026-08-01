namespace BotNexus.Domain.World;

/// <summary>
/// Capabilities a satellite node advertises.
///
/// <para><b>These are descriptive labels, not grants (#2606).</b> The gateway does not read them to
/// permit or refuse anything - there is no satellite dispatch surface for them to gate.</para>
/// </summary>
public enum SatelliteCapability
{
    /// <summary>Display desktop notifications (toast) for gateway events.</summary>
    Notify,

    /// <summary>Render HTML canvas content in a local window for conversations.</summary>
    Canvas,

    /// <summary>Execute commands sent by authorized agents (requires per-command approval on the satellite).</summary>
    Exec
}
