// ProviderCredentialOutcome.cs
namespace BotNexus.Gateway.Abstractions.Providers;

/// <summary>
/// Why a provider credential resolution ended the way it did (#3281).
///
/// <para>
/// <b>Why this type exists.</b> Credential resolution used to answer with a bare
/// <c>string?</c>, and <c>null</c> carried three unrelated meanings at once: the provider
/// was never configured, the provider was configured but the refresh call failed, and the
/// provider had no credential to begin with. Collapsing those into one value made a
/// seven-hour upstream outage indistinguishable from a provider nobody had set up. The
/// gateway logged 391 refresh failures during that window and no caller could act on a
/// single one of them, because by the time the value reached a caller the reason had
/// already been destroyed.
/// </para>
///
/// <para>
/// This is the same defect shape as the collapsed vector-scan report (#3244) and the
/// collapsed cron preflight boolean (#3210): when several distinguishable states fold into
/// one indistinguishable outcome, there is no defect to fix until the states are made
/// representable again. Making the reason part of the result is what lets a health observer
/// tell "down" from "absent".
/// </para>
/// </summary>
public enum ProviderCredentialStatus
{
    /// <summary>
    /// No credential is configured for this provider. This is a steady state, not a fault:
    /// it means nobody set the provider up, and it must never be reported as an outage.
    /// </summary>
    NotConfigured = 0,

    /// <summary>A credential was resolved successfully.</summary>
    Resolved = 1,

    /// <summary>
    /// A credential exists but refreshing it failed - typically an upstream error such as a
    /// 502/503 during token exchange. This is the state that indicates a provider outage,
    /// and the only one that should ever drive a degraded-health signal.
    /// </summary>
    RefreshFailed = 2
}

/// <summary>
/// The result of resolving a provider credential, carrying the reason alongside the value
/// so that a caller can distinguish an outage from an unconfigured provider (#3281).
/// </summary>
/// <param name="Status">Why the resolution ended as it did.</param>
/// <param name="ApiKey">The resolved credential, non-null only when <paramref name="Status"/> is <see cref="ProviderCredentialStatus.Resolved"/>.</param>
/// <param name="FailureClass">Exception type name when the refresh failed; otherwise null.</param>
/// <param name="StatusCode">Upstream HTTP status code when one could be determined; otherwise null.</param>
/// <param name="ErrorMessage">Human-readable failure detail when the refresh failed; otherwise null.</param>
public sealed record ProviderCredentialOutcome(
    ProviderCredentialStatus Status,
    string? ApiKey,
    string? FailureClass = null,
    int? StatusCode = null,
    string? ErrorMessage = null)
{
    /// <summary>A successful resolution carrying the credential.</summary>
    public static ProviderCredentialOutcome Success(string apiKey) =>
        new(ProviderCredentialStatus.Resolved, apiKey);

    /// <summary>No credential is configured. Deliberately distinct from <see cref="Failed"/>.</summary>
    public static ProviderCredentialOutcome NotConfigured() =>
        new(ProviderCredentialStatus.NotConfigured, null);

    /// <summary>A configured credential could not be refreshed - an outage signal.</summary>
    public static ProviderCredentialOutcome Failed(string failureClass, int? statusCode, string? errorMessage) =>
        new(ProviderCredentialStatus.RefreshFailed, null, failureClass, statusCode, errorMessage);

    /// <summary>
    /// True when this outcome represents a provider-side fault rather than an absent
    /// configuration. Only a fault may drive a degraded-health signal.
    /// </summary>
    public bool IsProviderFault => Status == ProviderCredentialStatus.RefreshFailed;
}
