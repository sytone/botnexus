using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Cron;

/// <summary>
/// Single shared normalisation boundary for cron webhook targets (#2552).
/// </summary>
/// <remarks>
/// <para>
/// Before #2552 <see cref="CronJob.WebhookUrl"/> was a bare <c>string?</c> copied verbatim from the
/// API request and from config-declared jobs straight into <c>cron.sqlite</c>. Nothing checked the
/// scheme and nothing rejected embedded userinfo, so <c>https://user:pass@host/hook</c> was stored
/// in cleartext and echoed back to the portal edit field.
/// </para>
/// <para>
/// This type is deliberately the <b>only</b> place a webhook URL is validated. Both the API
/// create/update path (<c>CronController</c> / <c>CronJobCreateRequest.ToCronJob</c>) and the
/// config-declared materialisation in <c>CronScheduler</c> call it, so the imperative and
/// declarative surfaces cannot drift apart the way five private <c>HashActor</c> copies did (#2515).
/// </para>
/// <para>
/// #2745: cron webhook delivery is a gateway egress surface, so the address classification is
/// delegated to the shared <see cref="SsrfValidator"/> rather than reimplemented here. The refusal
/// is surfaced through a distinct message so an operator can tell a blocked address class apart
/// from a scheme/credentials rejection.
/// </para>
/// <para>
/// #3779: #2745 routed this surface through the shared validator but omitted its
/// <c>additionalBlockedHosts</c> argument, so operator-configured hostname blocks were silently
/// inert here while <c>web_fetch</c> and BrowserTools enforced them. The list is now a required
/// parameter of the validating overload rather than an optional one: a caller must state, at the
/// call site, which configured policy applies. Passing <see langword="null"/> is still legal and
/// still means "no configured hosts" - but it is now a visible decision rather than an omission.
/// </para>
/// </remarks>
public static class CronWebhookUrl
{
    /// <summary>
    /// Attempts to normalise a webhook URL.
    /// </summary>
    /// <param name="value">Raw candidate value. <see langword="null"/>/whitespace is allowed - a webhook URL is optional.</param>
    /// <param name="normalized">Trimmed value on success, or <see langword="null"/> when the input was empty or invalid.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="value"/> is absent or is an absolute
    /// <c>http</c>/<c>https</c> URL carrying no userinfo and not targeting a blocked address class;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryNormalize(string? value, out string? normalized)
        => TryNormalize(value, blockedHosts: null, out normalized, out _);

    /// <summary>
    /// Attempts to normalise a webhook URL against an operator-configured blocked-host list (#3779).
    /// </summary>
    /// <param name="value">Raw candidate value. <see langword="null"/>/whitespace is allowed.</param>
    /// <param name="blockedHosts">
    /// Operator-configured hostnames refused on this egress surface, or <see langword="null"/> when
    /// none are configured. Matched exactly and case-insensitively by <see cref="SsrfValidator"/>.
    /// </param>
    /// <param name="normalized">Trimmed value on success, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the value is absent or acceptable.</returns>
    public static bool TryNormalize(string? value, IReadOnlyList<string>? blockedHosts, out string? normalized)
        => TryNormalize(value, blockedHosts, out normalized, out _);

    /// <summary>
    /// Attempts to normalise a webhook URL, reporting <b>which</b> rule rejected it.
    /// </summary>
    /// <remarks>
    /// #2745 clause 5: callers that surface the failure to a human (the API controller, config
    /// diagnostics) need to distinguish "this URL is malformed / carries credentials" from "this URL
    /// points at an address class the gateway refuses to dereference", because the two demand
    /// completely different corrective action from the operator.
    /// </remarks>
    /// <param name="value">Raw candidate value. <see langword="null"/>/whitespace is allowed.</param>
    /// <param name="normalized">Trimmed value on success, otherwise <see langword="null"/>.</param>
    /// <param name="rejectionReason">
    /// <see langword="null"/> on success; otherwise <see cref="RejectionMessage"/> for a
    /// scheme/credentials failure or <see cref="BlockedAddressRejectionMessage"/> for a blocked
    /// address class.
    /// </param>
    /// <returns><see langword="true"/> when the value is absent or acceptable.</returns>
    public static bool TryNormalize(string? value, out string? normalized, out string? rejectionReason)
        => TryNormalize(value, blockedHosts: null, out normalized, out rejectionReason);

    /// <summary>
    /// Attempts to normalise a webhook URL against an operator-configured blocked-host list,
    /// reporting <b>which</b> rule rejected it (#3779).
    /// </summary>
    /// <remarks>
    /// This is the single implementing overload; every other <c>TryNormalize</c> delegates here.
    /// A configured-host refusal is reported through <see cref="BlockedAddressRejectionMessage"/>
    /// because it is the same class of corrective action as an address-class refusal: change the
    /// target, not the URL's syntax.
    /// </remarks>
    /// <param name="value">Raw candidate value. <see langword="null"/>/whitespace is allowed.</param>
    /// <param name="blockedHosts">
    /// Operator-configured hostnames refused on this egress surface, or <see langword="null"/>/empty
    /// when none are configured - in which case behaviour is identical to pre-#3779.
    /// </param>
    /// <param name="normalized">Trimmed value on success, otherwise <see langword="null"/>.</param>
    /// <param name="rejectionReason">
    /// <see langword="null"/> on success; otherwise <see cref="RejectionMessage"/> for a
    /// scheme/credentials failure or <see cref="BlockedAddressRejectionMessage"/> for a blocked
    /// address class or configured host.
    /// </param>
    /// <returns><see langword="true"/> when the value is absent or acceptable.</returns>
    public static bool TryNormalize(
        string? value,
        IReadOnlyList<string>? blockedHosts,
        out string? normalized,
        out string? rejectionReason)
    {
        normalized = null;
        rejectionReason = null;

        // A webhook URL is optional on every job type; absence is not a validation failure.
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            rejectionReason = RejectionMessage;
            return false;
        }

        // Allow-list, not a deny-list: file:, ftp:, javascript: and everything else are rejected.
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = RejectionMessage;
            return false;
        }

        // HttpClient rejects URL userinfo before dispatch anyway. Failing here keeps a target that
        // could never deliver - but whose credentials would persist in cleartext - out of the store.
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            rejectionReason = RejectionMessage;
            return false;
        }

        // #2745: one shared SSRF policy for every gateway egress surface. Loopback, RFC-1918,
        // link-local/IMDS and cloud-metadata hosts are classified by SsrfValidator, never here -
        // a second copy of the address table is exactly the drift this boundary exists to prevent.
        // #3779: the configured host list travels WITH the call. SsrfValidator's parameter is
        // optional, and an omitted optional argument is invisible at review - which is exactly how
        // this surface enforced less than its configuration said for the whole of #2745's life.
        var ssrf = SsrfValidator.Validate(parsed, blockedHosts);
        if (!ssrf.IsSafe)
        {
            rejectionReason = BlockedAddressRejectionMessage;
            return false;
        }

        // Return the trimmed original rather than Uri.ToString() so a valid URL round-trips through
        // create/list/update byte-for-byte instead of being silently reshaped (escaping, trailing slash).
        normalized = trimmed;
        return true;
    }

    /// <summary>Standard rejection message surfaced to API callers and config diagnostics.</summary>
    public const string RejectionMessage =
        "WebhookUrl must be an absolute http or https URL and must not contain embedded credentials.";

    /// <summary>
    /// Rejection message for a syntactically valid URL that targets an address class the gateway
    /// refuses to dereference (loopback, private range, link-local, cloud metadata) or a hostname the
    /// operator has configured as blocked (#3779). Kept distinct from <see cref="RejectionMessage"/>
    /// so an operator can tell which rule fired (#2745).
    /// </summary>
    /// <remarks>
    /// #3779 deliberately does NOT re-word this constant. A configured-host refusal and an
    /// address-class refusal demand the same corrective action from the operator - change the target
    /// - so they share a message, and the wording stays byte-identical to pre-#3779 so an unconfigured
    /// deployment's API responses and log lines are unchanged.
    /// </remarks>
    public const string BlockedAddressRejectionMessage =
        "WebhookUrl targets a blocked address class (loopback, private, link-local or cloud-metadata) and is refused by the gateway SSRF policy.";
}
