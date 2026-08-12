using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Applies compiled regex patterns for common secret formats to redact credentials
/// from text before it is written to the session store. Each matched value is
/// replaced with <c>[REDACTED]</c>.
/// </summary>
public sealed partial class SecretRedactor : ISecretRedactor
{
    // Patterns are applied in order; more-specific patterns appear before generic ones.
    private static readonly Regex[] Patterns =
    [
        // OpenAI project keys (sk-proj-...)
        OpenAiProjectKeyRegex(),

        // OpenAI legacy keys (sk-...) — must come after project key to avoid partial matches
        OpenAiLegacyKeyRegex(),

        // Anthropic API keys (sk-ant-...)
        AnthropicKeyRegex(),

        // GitHub fine-grained personal access token (github_pat_...)
        GitHubFineGrainedPatRegex(),

        // GitHub classic tokens: ghp_, ghs_, gho_
        GitHubClassicTokenRegex(),

        // GitLab personal access tokens (glpat-...)
        GitLabPersonalAccessTokenRegex(),
        // GitLab routable token family (gldt-, glcbt-, glptt-, glft-, glimt-, glagent-,
        // glwt-, glsoat-, glffct-, glrt-, glrtr-)
        GitLabRoutableTokenRegex(),
        // GitLab runner registration tokens (GR1348941...)
        GitLabRunnerTokenRegex(),
        // GitLab session cookie (_gitlab_session=...)
        GitLabSessionCookieRegex(),

        // AWS access key IDs (AKIA...)
        AwsAccessKeyRegex(),
        // AWS secret access key VALUES via field name (aws_secret_access_key: "..." / SecretAccessKey=...)
        AwsSecretAccessKeyRegex(),

        // Google API keys (AIza...)
        GoogleApiKeyRegex(),

        // Slack tokens (xox...)
        SlackTokenRegex(),

        // Stripe live/test secret keys (sk_live_, sk_test_)
        StripeSecretKeyRegex(),

        // Telegram bot tokens (<digits>:<35-char base64url>) — must come before the generic
        // patterns so the whole token is redacted rather than a trailing fragment.
        TelegramBotTokenRegex(),

        // Authorization: Bearer <token> HTTP headers
        AuthorizationBearerRegex(),

        // Authorization: Basic <base64> HTTP headers
        AuthorizationBasicRegex(),

        // Authorization: Bot <token> HTTP headers (Discord-style)
        AuthorizationBotRegex(),

        // Proxy-Authorization: <scheme> <credential> HTTP headers
        ProxyAuthorizationRegex(),

        // X-Api-Key / X-Auth-Token / X-*-Token style header credentials
        ApiKeyStyleHeaderRegex(),

        // Standalone Bearer <token> not preceded by an Authorization header name
        StandaloneBearerRegex(),

        // Generic api_key / api-key = <value> patterns in text
        GenericApiKeyRegex(),
    ];

    // Trusted-only security-event sink (#1647). Optional: when null the redactor behaves exactly
    // as before (no emission). A Redact call that actually replaces at least one secret emits one
    // SecurityEvent to the trusted sink; the event carries only a non-sensitive reference and never
    // any plaintext secret material.
    private readonly ISecurityEventSink? _securityEvents;

    // Operator-supplied patterns (#2727). Applied AFTER the built-in set and never in place of it,
    // so a configuration mistake can add noise but can never switch redaction off. Compiled once at
    // construction with a match timeout, which is what keeps a pathological operator regex from
    // hanging the transcript writer.
    private readonly IReadOnlyList<Regex> _operatorPatterns;

    /// <summary>
    /// Creates a redactor. When a trusted <paramref name="securityEvents"/> sink is supplied, every
    /// <see cref="Redact(string)"/> call that replaces at least one secret emits exactly one
    /// <see cref="SecurityEvent"/>; without it the redactor behaves exactly as before (no emission).
    /// The sink is optional so existing callers and the DI type-mapped registration (which auto-
    /// resolves the registered sink) and tests that only exercise redaction need no changes.
    /// </summary>
    /// <param name="securityEvents">Trusted security-event sink, or null to disable emission.</param>
    /// <param name="options">
    /// Operator-supplied additional patterns (#2727), or null for the built-in set only. Compiled
    /// eagerly so an invalid pattern fails here - at startup, where an operator sees it - rather than
    /// mid-transcript, where it would either throw on the logging path or silently stop redacting.
    /// </param>
    /// <exception cref="ArgumentException">An operator pattern is empty, malformed, or matches everything.</exception>
    public SecretRedactor(ISecurityEventSink? securityEvents = null, SecretRedactionOptions? options = null)
    {
        _securityEvents = securityEvents;
        _operatorPatterns = options?.Compile() ?? [];
    }

    /// <inheritdoc />
    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;
        foreach (var pattern in Patterns)
            result = pattern.Replace(result, "[REDACTED]");

        result = ApplyOperatorPatterns(result);

        // Emit one trusted security event only when a secret was actually replaced. A no-op Redact
        // (nothing matched) emits nothing. The event carries a non-sensitive SecretRef reference and
        // never the matched value, so a redaction can never leak the secret it removed.
        if (!string.Equals(result, input, StringComparison.Ordinal))
            EmitRedaction();

        return result;
    }

    /// <summary>
    /// Redacts a command/agent output summary or diagnostic destined for external delivery
    /// (cron webhook / <c>cron_changed</c> fan-out, #1752). Applies every base <see cref="Redact(string)"/>
    /// secret pattern first, then additionally classifies action-required material that must never
    /// leave the box: device / verification codes become <c>[redacted-code]</c>, device action URLs
    /// (e.g. <c>Visit https://.../device and enter code ...</c>) become <c>[redacted-url]</c>, and
    /// <c>key=value</c> secrets (<c>token=</c>/<c>api_key=</c>/<c>password=</c>/<c>secret=</c>) have their
    /// value masked with <c>***</c>.
    ///
    /// MUST be applied to the external copy only; keep the full unredacted output for the local
    /// operator record. Returns <paramref name="input"/> unchanged when it is null/empty or nothing matched.
    /// </summary>
    public string RedactForExternalDelivery(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Action-required patterns run BEFORE the base secret sweep so that key=value secrets are
        // masked with *** (and device codes/URLs classified) rather than being swept up first by the
        // generic api_key pattern into [REDACTED].
        var result = input;

        // Device action URLs before key=value so a "https://.../device" is classified as a URL.
        result = DeviceActionUrlRegex().Replace(result, "[redacted-url]");

        // key=value action secrets: preserve the key name + separator, mask the value with ***.
        result = KeyValueSecretRegex().Replace(result, "${key}***");

        // Device / verification codes: the "enter code XXXX" phrase form first, then the bare
        // hyphenated code shape (e.g. WDJB-MJHT) that OAuth device flows print standalone.
        result = EnterCodePhraseRegex().Replace(result, "enter code [redacted-code]");
        result = HyphenatedDeviceCodeRegex().Replace(result, "[redacted-code]");

        // Base secret patterns last (they emit a security event when they replace anything).
        result = Redact(result);

        return result;
    }
    /// <summary>
    /// Applies the operator-supplied patterns (#2727) on top of the built-in sweep.
    /// </summary>
    /// <remarks>
    /// A timed-out pattern is skipped rather than propagated. That choice is deliberate: the caller
    /// is a transcript/log writer, and throwing from here would take down the very path that is
    /// trying to record what happened. Skipping degrades to "this one operator pattern did not
    /// redact" while every built-in pattern has already been applied, which is strictly safer than
    /// losing the write. Malformed patterns cannot reach this method at all - they are rejected at
    /// construction - so the only reachable fault here is the bounded timeout.
    /// </remarks>
    private string ApplyOperatorPatterns(string input)
    {
        if (_operatorPatterns.Count == 0)
            return input;

        var result = input;
        foreach (var pattern in _operatorPatterns)
        {
            try
            {
                result = pattern.Replace(result, "[REDACTED]");
            }
            catch (RegexMatchTimeoutException)
            {
                // Intentionally ignored - see remarks. The built-in patterns have already run.
            }
        }

        return result;
    }

    /// <summary>
    /// Records one <c>secret.redacted</c> event to the trusted sink. The target is a fixed,
    /// non-sensitive reference (the transcript being scrubbed) - never the matched secret value or a
    /// count that could narrow it. Best-effort: a null sink is a no-op and any sink fault is
    /// swallowed so the redaction path - which protects the session store - is never broken.
    /// </summary>
    private void EmitRedaction()
    {
        if (_securityEvents is null)
            return;

        try
        {
            var evt = new SecurityEvent(
                SecurityEventCategory.Secret,
                "secret.redacted",
                SecurityEventOutcome.Success,
                SecurityEventSeverity.Low,
                Target: new SecurityEventTarget(SecurityTargetKind.SecretRef, "transcript"),
                Control: SecurityControlFamily.Secret);
            _securityEvents.Record(evt);
        }
        catch
        {
            // Observability must never break the redaction path. The redactor takes no logger
            // (it is on the hot transcript-write path); a sink fault is simply swallowed.
        }
    }

    // ──────────────────── Compiled regex factories ────────────────────

    [GeneratedRegex(@"sk-proj-[A-Za-z0-9_\-]{40,}", RegexOptions.Compiled)]
    private static partial Regex OpenAiProjectKeyRegex();

    [GeneratedRegex(@"sk-[A-Za-z0-9]{48,}", RegexOptions.Compiled)]
    private static partial Regex OpenAiLegacyKeyRegex();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{40,}", RegexOptions.Compiled)]
    private static partial Regex AnthropicKeyRegex();

    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]{59,}", RegexOptions.Compiled)]
    private static partial Regex GitHubFineGrainedPatRegex();

    [GeneratedRegex(@"gh[pso]_[A-Za-z0-9]{36}", RegexOptions.Compiled)]
    private static partial Regex GitHubClassicTokenRegex();

    // GitLab personal access token (glpat-<20 chars>). Mirrors the GitHub prefix-token style.
    [GeneratedRegex(@"glpat-[A-Za-z0-9._=\-]{20}", RegexOptions.Compiled)]
    private static partial Regex GitLabPersonalAccessTokenRegex();

    // GitLab routable token family: a fixed set of two-to-five-letter prefixes (deploy, CI build,
    // pipeline trigger, feed, incoming mail, agent, webhook, SCIM OAuth, feature-flag client,
    // runner authentication, runner registration) followed by <20 chars>.
    [GeneratedRegex(@"gl(dt|cbt|ptt|ft|imt|agent|wt|soat|ffct|rt|rtr)-[A-Za-z0-9._\-]{20}", RegexOptions.Compiled)]
    private static partial Regex GitLabRoutableTokenRegex();

    // GitLab runner registration token (GR1348941<20 chars>).
    [GeneratedRegex(@"GR1348941[A-Za-z0-9_\-]{20}", RegexOptions.Compiled)]
    private static partial Regex GitLabRunnerTokenRegex();

    // GitLab session cookie (_gitlab_session=<20 chars>).
    [GeneratedRegex(@"_gitlab_session=[A-Za-z0-9%._\-]{20}", RegexOptions.Compiled)]
    private static partial Regex GitLabSessionCookieRegex();

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKeyRegex();

    // AWS secret access key VALUE, keyed off its field name so only the 40-char secret that
    // follows an aws_secret_access_key / SecretAccessKey label is redacted. The value shape
    // alone (40 base64-ish chars) is deliberately NOT matched standalone: too many innocent
    // 40-char hashes/ids would be false positives (#2286). Group 1 = field name + separator +
    // optional opening quote (preserved as context in RedactForExternalDelivery-style callers);
    // the base Redact sweep replaces the whole match with [REDACTED].
    [GeneratedRegex(@"(?i)(aws[-_]?secret[-_]?access[-_]?key|secretaccesskey)[""']?\s*[:=]\s*[""']?([A-Za-z0-9/+=]{40})", RegexOptions.Compiled)]
    private static partial Regex AwsSecretAccessKeyRegex();

    [GeneratedRegex(@"AIza[0-9A-Za-z\-_]{35}", RegexOptions.Compiled)]
    private static partial Regex GoogleApiKeyRegex();

    [GeneratedRegex(@"xox[bprao]-[A-Za-z0-9\-]+", RegexOptions.Compiled)]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"sk_(live|test)_[A-Za-z0-9]{20,}", RegexOptions.Compiled)]
    private static partial Regex StripeSecretKeyRegex();

    // Telegram bot tokens: a numeric bot id, a colon, then a 35-char base64url secret
    // (e.g. "123456789:AAExxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"). This id:secret shape is
    // distinctive enough to match without an anchor, and matching mid-string is required
    // because TelegramBotApiClient embeds the token directly after the literal "bot" in
    // its endpoint/file URLs (https://api.telegram.org/bot{token}/...). Whole-string
    // redaction (BotNexus never chunks) means the OpenClaw chunk-boundary variant does
    // not apply here.
    [GeneratedRegex(@"\d{6,12}:[A-Za-z0-9_\-]{35}", RegexOptions.Compiled)]
    private static partial Regex TelegramBotTokenRegex();

    // Capture group 1 = prefix up to and including "Bearer "; group 2 = the token itself.
    // Replace entire match so the header name is preserved: "Authorization: Bearer [REDACTED]"
    [GeneratedRegex(@"(Authorization""?\s*[:=]?\s*""?\s*Bearer\s+)[A-Za-z0-9+/=._\-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationBearerRegex();

    // Authorization: Basic <base64>. Basic auth base64-encodes the full "user:password";
    // a logged/exception-captured Basic header would otherwise land in the session store
    // unredacted. The optional quote/colon allowance ("Authorization": "Basic ...") handles
    // the serialized/JSON-embedded header form.
    [GeneratedRegex(@"(Authorization""?\s*[:=]?\s*""?\s*Basic\s+)[A-Za-z0-9+/=]{16}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationBasicRegex();

    // Authorization: Bot <token> (Discord-style). Same serialized/quoted hardening as Basic.
    [GeneratedRegex(@"(Authorization""?\s*[:=]?\s*""?\s*Bot\s+)[A-Za-z0-9._\-+=]{18}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationBotRegex();

    // Proxy-Authorization: <scheme> <opaque credential>. Any single-word scheme
    // (Basic/Bearer/Negotiate/NTLM/...) followed by an opaque credential.
    [GeneratedRegex(@"(Proxy-Authorization""?\s*[:=]?\s*""?\s*\w+\s+)[A-Za-z0-9+/=._\-]{16}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ProxyAuthorizationRegex();

    // Header-style API-key credentials the generic api[_-]?key shape misses: X-Api-Key,
    // X-Auth-Token, X-OpenClaw-Token, and the broader X-*-Token / X-*-Key family. Preserves
    // the header name and redacts the value.
    [GeneratedRegex(@"(X-(?:Api-Key|Auth-Token|OpenClaw-Token|[A-Za-z0-9]+-(?:Token|Key))""?\s*[:=]\s*""?)[^\s""',;]{8}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyStyleHeaderRegex();

    // Standalone "Bearer <token>" that is NOT preceded by an Authorization header name -
    // e.g. a raw HttpRequestException / diagnostic that prints "Bearer eyJ..." outside a
    // full header line. The negative lookbehind avoids double-processing header forms already
    // covered by AuthorizationBearerRegex, and the {18} length floor keeps the word "Bearer"
    // in ordinary prose untouched.
    [GeneratedRegex(@"(?<!Authorization""?\s*[:=]?\s*""?\s*)\bBearer\s+[A-Za-z0-9._\-+=]{18}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneBearerRegex();

    // Handles: api_key=VALUE, api-key: VALUE, apiKey=VALUE  (case-insensitive key name)
    [GeneratedRegex(@"(?i)api[_\-]?key\s*[=:]\s*[A-Za-z0-9+/=._\-]{20,}", RegexOptions.Compiled)]
    private static partial Regex GenericApiKeyRegex();
    // -------------- External-delivery action-required patterns (#1752) --------------

    // Device action URL: an http(s) URL whose path ends in /device (optionally trailing slash or
    // query), as emitted by OAuth device-authorization flows
    // (e.g. "Visit https://github.com/login/device and enter code ..."). Redacted to [redacted-url].
    [GeneratedRegex(@"https?://[^\s""'<>]*?/device(?:/|\?[^\s""'<>]*)?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DeviceActionUrlRegex();

    // "enter code <value>" verification phrase. The code is a run of 4+ alphanumerics (optionally
    // hyphenated). Only the value is replaced so the surrounding instruction survives.
    [GeneratedRegex(@"(?i)enter\s+code\s+[A-Za-z0-9][A-Za-z0-9\-]{3,}", RegexOptions.Compiled)]
    private static partial Regex EnterCodePhraseRegex();

    // Bare hyphenated device code shape: two-or-more groups of 4 alphanumerics joined by hyphens
    // (e.g. WDJB-MJHT, ABCD-1234). The fixed 4-4 grouped shape is the discriminator OAuth device
    // flows use; ordinary hyphenated prose words do not match it.
    [GeneratedRegex(@"\b[A-Za-z0-9]{4,}(?:-[A-Za-z0-9]{4,})+\b", RegexOptions.Compiled)]
    private static partial Regex HyphenatedDeviceCodeRegex();

    // key=value action secrets: token / api_key / api-key / apikey / password / passwd / pwd /
    // secret, '=' or ':' separator, then a value of 4+ non-delimiter chars. Named group "key"
    // captures the key + separator so it is preserved while the value is masked with ***.
    [GeneratedRegex(@"(?<key>(?i:token|api[_\-]?key|password|passwd|pwd|secret)\s*[=:]\s*)[^\s""',;]{4,}", RegexOptions.Compiled)]
    private static partial Regex KeyValueSecretRegex();
}