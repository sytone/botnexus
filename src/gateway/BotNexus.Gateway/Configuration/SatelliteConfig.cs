using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Configuration for a registered satellite node. Stored in <c>config.json</c> under
/// <c>gateway.satellites.{id}</c>.
/// </summary>
public sealed class SatelliteConfig
{
    /// <summary>Human-readable display name for this satellite.</summary>
    [Display(
        Name = "Display name",
        Description = "Human-readable display name for this satellite.",
        GroupName = "Satellite",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "satellite", Order = 0)]
    public string? DisplayName { get; set; }

    /// <summary>Platform the satellite runs on. Values: <c>windows</c>, <c>macos</c>, <c>linux</c>.</summary>
    [Display(
        Name = "Platform",
        Description = "Platform the satellite runs on. One of windows, macos, or linux.",
        GroupName = "Satellite",
        Order = 1)]
    [DefaultValue("windows")]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "satellite", Order = 1)]
    public string Platform { get; set; } = "windows";

    /// <summary>
    /// API key the satellite uses to authenticate with the gateway.
    /// Generated via <c>botnexus satellite register</c> and never displayed again after creation.
    /// Prefixed with <c>sat_</c> for identification.
    /// </summary>
    [Display(
        Name = "API key",
        Description = "API key the satellite uses to authenticate with the gateway. Sensitive: stored and shown masked.",
        GroupName = "Satellite",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "satellite", Order = 2, Secret = true)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Capabilities this satellite is allowed to perform.
    /// Valid values: <c>notify</c>, <c>canvas</c>, <c>exec</c>.
    /// </summary>
    public List<string>? Capabilities { get; set; }

    /// <summary>User ID of the satellite's owner. Events are filtered server-side to this user's conversations.</summary>
    [Display(
        Name = "Owner user ID",
        Description = "User ID of the satellite's owner. Events are filtered server-side to this user's conversations.",
        GroupName = "Satellite",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "satellite", Order = 3)]
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// Seconds without a heartbeat before the satellite is marked stale.
    /// Defaults to 120 seconds.
    /// </summary>
    [Display(
        Name = "Stale timeout (seconds)",
        Description = "Seconds without a heartbeat before the satellite is marked stale.",
        GroupName = "Satellite",
        Order = 4)]
    [DefaultValue(120)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "satellite", Order = 4)]
    public int StaleTimeoutSeconds { get; set; } = 120;

    /// <summary>Whether this satellite is enabled. Disabled satellites are rejected on connect.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this satellite is enabled. Disabled satellites are rejected on connect.",
        GroupName = "Satellite",
        Order = 5)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "satellite", Order = 5)]
    public bool Enabled { get; set; } = true;
}
