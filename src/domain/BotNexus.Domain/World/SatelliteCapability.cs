namespace BotNexus.Domain.World;

/// <summary>
/// Capabilities a satellite node can perform. Each must be explicitly granted during registration.
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
