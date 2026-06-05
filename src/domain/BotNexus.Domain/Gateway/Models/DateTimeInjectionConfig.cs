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
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// IANA timezone ID used when formatting the injected datetime.
    /// Examples: <c>"UTC"</c>, <c>"America/Los_Angeles"</c>, <c>"Europe/London"</c>.
    /// Falls back to the gateway <c>DefaultTimezone</c> setting, then UTC, when null or empty.
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>
    /// Output format. Only <c>"iso8601"</c> is supported today; reserved for future extension
    /// to <c>"rfc2822"</c> or fully custom patterns.
    /// </summary>
    public string Format { get; set; } = "iso8601";
}
