using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Constant-time comparison helpers for secret material. Consolidates the timing-safe
/// comparison intent already used by the webhook secret paths so that credential
/// verification does not leak information through data-dependent runtime.
/// </summary>
public static class TimingSafe
{
    /// <summary>
    /// Compares two strings for equality in constant time with respect to their contents,
    /// over their UTF-8 byte representations. Returns <c>false</c> for null inputs or when
    /// lengths differ (length is not secret and <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// requires equal-length spans).
    /// </summary>
    public static bool Equals(string? a, string? b)
    {
        if (a is null || b is null)
            return false;

        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
