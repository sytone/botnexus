using System.Text.Json.Serialization;

namespace BotNexus.Agent.Providers.Copilot.Discovery;

/// <summary>
/// Response shape of <c>GET https://api.github.com/copilot_internal/user</c>.
/// Identifies the authenticated user, their Copilot plan, and the API endpoints
/// (enterprise vs individual) that downstream requests must target. The
/// <see cref="QuotaSnapshots"/> field carries the per-feature usage allowance
/// surfaced by the <c>copilot quota</c> CLI command.
/// </summary>
public sealed class CopilotUserInfo
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("copilot_plan")]
    public string? CopilotPlan { get; set; }

    [JsonPropertyName("chat_enabled")]
    public bool ChatEnabled { get; set; }

    [JsonPropertyName("cli_enabled")]
    public bool CliEnabled { get; set; }

    [JsonPropertyName("access_type_sku")]
    public string? AccessTypeSku { get; set; }

    [JsonPropertyName("assigned_date")]
    public string? AssignedDate { get; set; }

    [JsonPropertyName("organization_login_list")]
    public List<string>? OrganizationLoginList { get; set; }

    [JsonPropertyName("endpoints")]
    public CopilotEndpoints? Endpoints { get; set; }

    [JsonPropertyName("quota_reset_date")]
    public string? QuotaResetDate { get; set; }

    [JsonPropertyName("quota_snapshots")]
    public Dictionary<string, CopilotQuotaSnapshot>? QuotaSnapshots { get; set; }
}

/// <summary>
/// API endpoints returned by the user-info call. <see cref="Api"/> is the base
/// URL all downstream Copilot LLM requests must be sent to.
/// </summary>
public sealed class CopilotEndpoints
{
    [JsonPropertyName("api")]
    public string? Api { get; set; }

    [JsonPropertyName("proxy")]
    public string? Proxy { get; set; }

    [JsonPropertyName("telemetry")]
    public string? Telemetry { get; set; }

    [JsonPropertyName("origin-tracker")]
    public string? OriginTracker { get; set; }
}

/// <summary>
/// Per-feature quota snapshot as reported by GitHub Copilot. Common quota IDs
/// observed in captures: <c>chat</c>, <c>completions</c>,
/// <c>premium_interactions</c>.
/// </summary>
public sealed class CopilotQuotaSnapshot
{
    [JsonPropertyName("quota_id")]
    public string? QuotaId { get; set; }

    [JsonPropertyName("percent_remaining")]
    public double PercentRemaining { get; set; }

    [JsonPropertyName("quota_remaining")]
    public double QuotaRemaining { get; set; }

    [JsonPropertyName("entitlement")]
    public double Entitlement { get; set; }

    [JsonPropertyName("overage_count")]
    public double OverageCount { get; set; }

    [JsonPropertyName("overage_permitted")]
    public bool OveragePermitted { get; set; }

    [JsonPropertyName("unlimited")]
    public bool Unlimited { get; set; }
}
