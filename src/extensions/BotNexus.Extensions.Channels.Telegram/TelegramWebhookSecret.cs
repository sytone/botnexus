using BotNexus.Domain.Security;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Telegram-specific helpers over the shared <see cref="WebhookSecret"/> domain type.
/// </summary>
/// <remarks>
/// <para>
/// Telegram's <c>setWebhook</c> accepts an optional <c>secret_token</c> which Telegram then sends
/// back in the <c>X-Telegram-Bot-Api-Secret-Token</c> header on every update POST. Validating that
/// header is the sole mechanism that authenticates inbound webhook traffic: without it, anyone who
/// discovers the public webhook URL can POST forged updates straight into the agent pipeline. The
/// allowed character set is restricted by the Bot API to <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>,
/// <c>_</c> and <c>-</c>, with a length of 1–256 characters.
/// </para>
/// <para>
/// Those are exactly the rules <see cref="WebhookSecret"/> enforces at construction, so this type no
/// longer carries validation, generation or comparison logic of its own — the parity is deliberate
/// and the shared type is the single implementation. What remains here are two thin adapters over
/// the channel's raw string surfaces: configuration (<see cref="TryFromConfiguration"/>) and the
/// inbound header (<see cref="Matches"/>).
/// </para>
/// </remarks>
internal static class TelegramWebhookSecret
{
    /// <summary>
    /// Generates a cryptographically strong, URL-safe secret token within the Bot API's allowed
    /// character set and length bounds.
    /// </summary>
    public static WebhookSecret Generate() => WebhookSecret.Generate();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a syntactically valid
    /// Telegram webhook secret token (non-empty, ≤256 chars, allowed character set only).
    /// </summary>
    /// <remarks>
    /// Retained as a predicate over the raw configuration string because config binding produces a
    /// <see cref="string"/>; it is a thin wrapper over <see cref="WebhookSecret.TryCreate"/> so there
    /// is exactly one definition of "valid".
    /// </remarks>
    public static bool IsValid(string? value) => WebhookSecret.TryCreate(value, out _);

    /// <summary>
    /// Attempts to turn a configured <c>webhookSecretToken</c> into a validated secret.
    /// </summary>
    public static bool TryFromConfiguration(string? configuredValue, out WebhookSecret secret)
        => WebhookSecret.TryCreate(configuredValue, out secret);

    /// <summary>
    /// Compares the registered secret against the value supplied in an inbound request header in
    /// constant time, so a mismatch reveals nothing about how many leading characters matched.
    /// </summary>
    /// <param name="expected">The secret registered with Telegram for this bot. A <c>default</c>
    /// instance (bot not in webhook mode) never matches.</param>
    /// <param name="provided">The raw <c>X-Telegram-Bot-Api-Secret-Token</c> header value, or null when absent.</param>
    /// <returns><see langword="true"/> only when both are present and equal.</returns>
    /// <remarks>
    /// The header value is untrusted input, so it stays a <see cref="string"/> up to this point and
    /// is parsed here. A header that is not even a syntactically valid token is rejected before the
    /// comparison — and because <see cref="WebhookSecret.Equals(WebhookSecret)"/> compares SHA-256
    /// digests, the comparison itself is length- and content-independent.
    /// </remarks>
    public static bool Matches(WebhookSecret expected, string? provided)
        => expected.HasValue
           && WebhookSecret.TryCreate(provided, out var candidate)
           && Matches(expected, candidate);

    /// <summary>
    /// Constant-time comparison of two already-validated secrets.
    /// </summary>
    public static bool Matches(WebhookSecret expected, WebhookSecret provided)
        => expected.HasValue && provided.HasValue && expected.Equals(provided);
}
