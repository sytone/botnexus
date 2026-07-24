namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Redacts secret-shaped values from text before it is written to the session store.
/// Applied inline at every transcript append and compaction summary write to prevent
/// credentials, API keys, and tokens from being persisted unredacted.
/// </summary>
public interface ISecretRedactor
{
    /// <summary>
    /// Returns a copy of <paramref name="input"/> with any detected secrets replaced
    /// by <c>[REDACTED]</c>. Returns the original string unchanged when no secrets are detected.
    /// </summary>
    string Redact(string input);

    /// <summary>
    /// Redacts a command/agent output summary or diagnostic destined for <b>external delivery</b>
    /// (cron webhook / <c>cron_changed</c> fan-out). In addition to every base <see cref="Redact(string)"/>
    /// secret pattern, this classifies <b>action-required</b> material that must never leave the box:
    /// bare device / verification codes (replaced with <c>[redacted-code]</c>) and device action URLs
    /// such as <c>Visit https://.../device and enter code ...</c> (URL replaced with <c>[redacted-url]</c>),
    /// plus <c>key=value</c> secrets (<c>token=</c>/<c>api_key=</c>/<c>password=</c>/<c>secret=</c>) whose
    /// value is replaced with <c>***</c>.
    ///
    /// This MUST be applied to the external copy of any cron summary before it crosses the external-
    /// delivery boundary; the local operator record keeps the full unredacted output. Returns the
    /// original string unchanged when nothing matches, and returns <paramref name="input"/> as-is when
    /// it is null or empty.
    /// </summary>
    string RedactForExternalDelivery(string input);
}
