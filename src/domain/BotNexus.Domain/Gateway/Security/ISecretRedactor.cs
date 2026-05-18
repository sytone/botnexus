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
}
