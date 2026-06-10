using System.Security.Cryptography;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Generates cryptographically secure API keys for satellite registration.
/// </summary>
public static class SatelliteKeyGenerator
{
    private const string Prefix = "sat_";
    private const int KeyByteLength = 32;

    // Base62 alphabet: 0-9, A-Z, a-z (no confusing symbols)
    private static readonly char[] Base62Chars =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

    /// <summary>
    /// Generates a new satellite API key. Format: <c>sat_&lt;43 base62 chars&gt;</c>.
    /// The key is shown to the user exactly once during registration and cannot be recovered.
    /// </summary>
    /// <returns>A cryptographically random satellite API key.</returns>
    public static string GenerateApiKey()
    {
        Span<byte> bytes = stackalloc byte[KeyByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + ToBase62(bytes);
    }

    private static string ToBase62(ReadOnlySpan<byte> bytes)
    {
        // Convert bytes to base62 characters using modular arithmetic.
        // This produces ~43 characters from 32 bytes (256 bits of entropy).
        var chars = new char[43];
        var bigInt = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);

        for (int i = chars.Length - 1; i >= 0; i--)
        {
            bigInt = System.Numerics.BigInteger.DivRem(bigInt, 62, out var remainder);
            chars[i] = Base62Chars[(int)remainder];
        }

        return new string(chars);
    }
}
