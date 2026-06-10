using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Configuration for a registered satellite node. Stored in <c>config.json</c> under
/// <c>gateway.satellites.{id}</c>.
/// </summary>
public sealed class SatelliteConfig
{
    /// <summary>Human-readable display name for this satellite.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Platform the satellite runs on. Values: <c>windows</c>, <c>macos</c>, <c>linux</c>.</summary>
    public string Platform { get; set; } = "windows";

    /// <summary>
    /// API key the satellite uses to authenticate with the gateway.
    /// Generated via <c>botnexus satellite register</c> and never displayed again after creation.
    /// Prefixed with <c>sat_</c> for identification.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Capabilities this satellite is allowed to perform.
    /// Valid values: <c>notify</c>, <c>canvas</c>, <c>exec</c>.
    /// </summary>
    public List<string>? Capabilities { get; set; }

    /// <summary>User ID of the satellite's owner. Events are filtered server-side to this user's conversations.</summary>
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// Seconds without a heartbeat before the satellite is marked stale.
    /// Defaults to 120 seconds.
    /// </summary>
    public int StaleTimeoutSeconds { get; set; } = 120;

    /// <summary>Whether this satellite is enabled. Disabled satellites are rejected on connect.</summary>
    public bool Enabled { get; set; } = true;
}
