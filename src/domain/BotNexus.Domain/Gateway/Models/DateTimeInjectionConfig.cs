using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Datetime injection configuration. When enabled, the current datetime is prepended to every
/// user message sent to the LLM inside a <c>&lt;currentdatetime&gt;</c> XML tag so the agent
/// always has reliable temporal context.
/// </summary>
/// <remarks>
/// The world-level default lives on <c>GatewaySettingsConfig.DateTimeInjection</c>. Per-agent
/// overrides live on <c>AgentDescriptor.DateTimeInjection</c> and take precedence.
/// </remarks>
public sealed class DateTimeInjectionConfig
{
    /// <summary>
    /// Whether datetime injection is enabled for this agent/gateway.
    /// Defaults to <c>false</c> (opt-in). Set to <c>true</c> to activate.
    /// </summary>
    [Display(
        Name = "Enable datetime injection",
        Description = "When on, the current datetime is prepended to every user message so the agent always has reliable temporal context.",
        GroupName = "Datetime injection",
        Order = 0)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "datetime-injection", Order = 0)]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// IANA timezone ID used when formatting the injected datetime.
    /// Examples: <c>"UTC"</c>, <c>"America/Los_Angeles"</c>, <c>"Europe/London"</c>.
    /// Falls back to the gateway <c>DefaultTimezone</c> setting, then UTC, when null or empty.
    /// </summary>
    [Display(
        Name = "Timezone",
        Description = "IANA timezone ID used when formatting the injected datetime (for example UTC or America/Los_Angeles). Falls back to the gateway default timezone, then UTC, when blank.",
        GroupName = "Datetime injection",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "datetime-injection", Order = 1)]
    public string? Timezone { get; set; }

    /// <summary>
    /// Output format. Only <c>"iso8601"</c> is supported today; reserved for future extension
    /// to <c>"rfc2822"</c> or fully custom patterns.
    /// </summary>
    [Display(
        Name = "Datetime format",
        Description = "Output format for the injected datetime. Only iso8601 is supported today; rfc2822 and custom patterns are reserved for future use.",
        GroupName = "Datetime injection",
        Order = 2)]
    [DefaultValue("iso8601")]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "datetime-injection", Order = 2)]
    public string Format { get; set; } = "iso8601";
}
