using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Helpers for generating and verifying webhook HMAC-SHA256 signatures, and for
/// creating registration secrets. Follows the same convention as GitHub and Stripe:
/// the signature header value is <c>sha256=&lt;hex&gt;</c>.
///
/// <para>
/// <strong>Secret storage:</strong> BotNexus stores the plaintext webhook secret
/// in the SQLite database (protected by OS-level file permissions), consistent with
/// how the gateway API token is stored in config.json. This allows HMAC verification
/// without any key-encryption infrastructure. The plaintext secret is shown to the
/// user exactly once at registration and is not exposed again via the API.
/// </para>
/// </summary>
public static class WebhookSecretHelper
{
    private const string SignaturePrefix = "sha256=";

    /// <summary>
    /// Generates a cryptographically random plaintext secret suitable for use
    /// as a webhook shared secret. Format: <c>whsec_&lt;64 hex chars&gt;</c>.
    /// </summary>
    public static string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "whsec_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the <c>sha256=&lt;hex&gt;</c> HMAC signature for <paramref name="rawBody"/>
    /// using <paramref name="secret"/> as the key. This is what the caller should send
    /// in the <c>X-BotNexus-Signature-256</c> header, and what the gateway recomputes
    /// server-side to verify authenticity.
    /// </summary>
    public static string ComputeSignature(string secret, ReadOnlySpan<byte> rawBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(keyBytes, rawBody);
        return SignaturePrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies that <paramref name="signatureHeader"/> matches the expected HMAC
    /// computed from <paramref name="rawBody"/> and <paramref name="secret"/>.
    /// Uses constant-time comparison to resist timing attacks.
    /// </summary>
    /// <returns><c>true</c> if valid; <c>false</c> otherwise.</returns>
    public static bool VerifySignature(string secret, ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        var expected = ComputeSignature(secret, rawBody);

        // Constant-time compare — both strings are ASCII hex so byte-equality is correct.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signatureHeader));
    }
}
