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
    /// <c>http</c>/<c>https</c> URL carrying no userinfo; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;

        // A webhook URL is optional on every job type; absence is not a validation failure.
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
            return false;

        // Allow-list, not a deny-list: file:, ftp:, javascript: and everything else are rejected.
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // HttpClient rejects URL userinfo before dispatch anyway. Failing here keeps a target that
        // could never deliver - but whose credentials would persist in cleartext - out of the store.
        if (!string.IsNullOrEmpty(parsed.UserInfo))
            return false;

        // Return the trimmed original rather than Uri.ToString() so a valid URL round-trips through
        // create/list/update byte-for-byte instead of being silently reshaped (escaping, trailing slash).
        normalized = trimmed;
        return true;
    }

    /// <summary>Standard rejection message surfaced to API callers and config diagnostics.</summary>
    public const string RejectionMessage =
        "WebhookUrl must be an absolute http or https URL and must not contain embedded credentials.";
}
