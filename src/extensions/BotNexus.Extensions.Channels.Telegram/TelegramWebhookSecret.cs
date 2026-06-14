using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Generation and constant-time validation of the Telegram webhook secret token.
/// </summary>
/// <remarks>
/// Telegram's <c>setWebhook</c> accepts an optional <c>secret_token</c> which Telegram then sends
/// back in the <c>X-Telegram-Bot-Api-Secret-Token</c> header on every update POST. Validating that
/// header is the sole mechanism that authenticates inbound webhook traffic: without it, anyone who
/// discovers the public webhook URL can POST forged updates straight into the agent pipeline. The
/// allowed character set is restricted by the Bot API to <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>,
/// <c>_</c> and <c>-</c>, with a length of 1–256 characters.
/// </remarks>
internal static class TelegramWebhookSecret
{
    // 32 bytes of entropy → 43 url-safe base64 characters, comfortably within the 1–256 limit and
    // far beyond brute-force reach. All emitted characters are in the Bot API's allowed set.
    private const int EntropyBytes = 32;

    /// <summary>
    /// Characters permitted by the Telegram Bot API for the webhook <c>secret_token</c>.
    /// </summary>
    private static bool IsAllowedChar(char c)
        => (c >= 'A' && c <= 'Z')
           || (c >= 'a' && c <= 'z')
           || (c >= '0' && c <= '9')
           || c == '_'
           || c == '-';

    /// <summary>
    /// Generates a cryptographically strong, URL-safe secret token within the Bot API's allowed
    /// character set and length bounds.
    /// </summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(EntropyBytes);
        // URL-safe base64 (RFC 4648 §5): replace '+'/'/' with '-'/'_', strip '=' padding.
        // Every resulting character is in the Bot API's allowed set.
        return System.Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a syntactically valid
    /// Telegram webhook secret token (non-empty, ≤256 chars, allowed character set only).
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256)
            return false;

        foreach (var c in value)
        {
            if (!IsAllowedChar(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Compares an expected secret against the value supplied in an inbound request header in
    /// constant time, so a mismatch reveals nothing about how many leading characters matched.
    /// </summary>
    /// <param name="expected">The secret registered with Telegram for this bot. Never null/empty in practice.</param>
    /// <param name="provided">The raw <c>X-Telegram-Bot-Api-Secret-Token</c> header value, or null when absent.</param>
    /// <returns><see langword="true"/> only when both are present and byte-for-byte equal.</returns>
    /// <remarks>
    /// Both inputs are SHA-256 hashed before comparison so the fixed-length digest comparison can
    /// never short-circuit on a length difference — the comparison time is independent of both the
    /// length and the content of <paramref name="provided"/>.
    /// </remarks>
    public static bool Matches(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;

        Span<byte> expectedHash = stackalloc byte[32];
        Span<byte> providedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), providedHash);
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }
}
