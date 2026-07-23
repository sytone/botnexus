using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Security;

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

        // AWS access key IDs (AKIA...)
        AwsAccessKeyRegex(),

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

    /// <summary>
    /// Creates a redactor. When a trusted <paramref name="securityEvents"/> sink is supplied, every
    /// <see cref="Redact(string)"/> call that replaces at least one secret emits exactly one
    /// <see cref="SecurityEvent"/>; without it the redactor behaves exactly as before (no emission).
    /// The sink is optional so existing callers and the DI type-mapped registration (which auto-
    /// resolves the registered sink) and tests that only exercise redaction need no changes.
    /// </summary>
    /// <param name="securityEvents">Trusted security-event sink, or null to disable emission.</param>
    public SecretRedactor(ISecurityEventSink? securityEvents = null)
        => _securityEvents = securityEvents;

    /// <inheritdoc />
    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;
        foreach (var pattern in Patterns)
            result = pattern.Replace(result, "[REDACTED]");

        // Emit one trusted security event only when a secret was actually replaced. A no-op Redact
        // (nothing matched) emits nothing. The event carries a non-sensitive SecretRef reference and
        // never the matched value, so a redaction can never leak the secret it removed.
        if (!string.Equals(result, input, StringComparison.Ordinal))
            EmitRedaction();

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

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKeyRegex();

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
}
