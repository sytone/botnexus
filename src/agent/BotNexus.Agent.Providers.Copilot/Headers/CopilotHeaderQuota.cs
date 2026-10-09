namespace BotNexus.Agent.Providers.Copilot.Headers;

/// <summary>Header-only quota facts, not the account API's remaining quota. Null numeric values are unknown, invalid, or sentinel-valued.</summary>
/// <param name="Entitlement">The nonnegative <c>ent</c> entitlement count.</param>
/// <param name="RemainingPercent">The <c>rem</c> percentage, from zero to 100; not a remaining count.</param>
/// <param name="TotalRemainingCount">The nonnegative <c>totRem</c> remaining count.</param>
/// <param name="OverageCount">The nonnegative <c>ov</c> overage count.</param>
/// <param name="OveragePermitted">The <c>ovPerm</c> permission, when valid.</param>
/// <param name="ResetAt">The <c>rst</c> reset instant with an explicit offset.</param>
/// <param name="IsUnlimited">True only for the established <c>ent=-1&amp;totRem=-1</c> sentinel pair; false for two valid finite counts; otherwise unknown.</param>
public sealed record CopilotHeaderQuota(
    decimal? Entitlement = null,
    decimal? RemainingPercent = null,
    decimal? TotalRemainingCount = null,
    decimal? OverageCount = null,
    bool? OveragePermitted = null,
    DateTimeOffset? ResetAt = null,
    bool? IsUnlimited = null)
{
    /// <summary>No valid allowlisted quota facts were available; no values are inferred.</summary>
    public static CopilotHeaderQuota Unknown { get; } = new();
}

/// <summary>The quota snapshot header that supplied an observation.</summary>
public enum CopilotQuotaDimension
{
    /// <summary>Facts from <c>x-quota-snapshot-chat</c>.</summary>
    Chat,
    /// <summary>Facts from <c>x-quota-snapshot-completions</c>.</summary>
    Completions,
    /// <summary>Facts from <c>x-quota-snapshot-premium_interactions</c>.</summary>
    PremiumInteractions
}

/// <summary>Provenance of quota facts; distinct from account API responses.</summary>
public enum CopilotQuotaSource
{
    /// <summary>Allowlisted HTTP or WebSocket handshake response headers.</summary>
    ResponseHeaders
}

/// <summary>A response-header observation ordered first by logical request start, then by observation within that request.</summary>
/// <param name="Scope">Verified non-secret account attribution captured at logical request start.</param>
/// <param name="Dimension">The source snapshot header.</param>
/// <param name="RequestOrder">Process-local logical request-start order; retries retain this value.</param>
/// <param name="ObservedAt">The observation time, not an ordering authority.</param>
/// <param name="Quota">Typed header facts without the raw header or credentials.</param>
/// <param name="ObservationOrder">Increasing response observation ordinal within the logical request, including retries and transport fallback.</param>
public sealed record CopilotHeaderObservation(
    CopilotHeaderScope Scope,
    CopilotQuotaDimension Dimension,
    long RequestOrder,
    DateTimeOffset ObservedAt,
    CopilotHeaderQuota Quota,
    long ObservationOrder = 0)
{
    /// <summary>These facts come only from response headers, never from the account API.</summary>
    public CopilotQuotaSource Source => CopilotQuotaSource.ResponseHeaders;
}

/// <summary>Optional in-process observer; receives typed, non-secret facts only.</summary>
public interface ICopilotHeaderSink
{
    /// <summary>Receives a bounded typed snapshot without retaining raw provider data.</summary>
    void Observe(CopilotHeaderObservation observation);
    /// <summary>Records an unattributed legacy response without assigning it to a default account.</summary>
    void ObserveLegacyUnattributed();
}
