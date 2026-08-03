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
        => TryNormalize(value, out normalized, out _);

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
        var ssrf = SsrfValidator.Validate(parsed);
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
    /// refuses to dereference (loopback, private range, link-local, cloud metadata). Kept distinct
    /// from <see cref="RejectionMessage"/> so an operator can tell which rule fired (#2745).
    /// </summary>
    public const string BlockedAddressRejectionMessage =
        "WebhookUrl targets a blocked address class (loopback, private, link-local or cloud-metadata) and is refused by the gateway SSRF policy.";
}
