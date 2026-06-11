using System.Net;
using System.Net.Sockets;

namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Shared SSRF (Server-Side Request Forgery) validation utility.
/// Validates URLs against private network ranges, cloud metadata endpoints,
/// and configurable blocked hosts before opening outbound connections.
/// <para>
/// Use this for ANY URL that will be used to open a new connection —
/// not just user-provided URLs but also dynamically-discovered URLs from
/// intermediary services (CDP /json/list, proxy headers, redirect targets, etc.).
/// </para>
/// </summary>
public static class SsrfValidator
{
    /// <summary>
    /// Validates that a URI does not target private, loopback, link-local,
    /// cloud metadata, or otherwise reserved network addresses.
    /// </summary>
    /// <param name="uri">The URI to validate.</param>
    /// <param name="additionalBlockedHosts">
    /// Optional additional hostnames to block (exact match, case-insensitive).
    /// </param>
    /// <returns>
    /// An <see cref="SsrfValidationResult"/> indicating whether the URL is safe
    /// or the reason it was blocked.
    /// </returns>
    public static SsrfValidationResult Validate(Uri uri, IReadOnlyList<string>? additionalBlockedHosts = null)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // Only HTTP(S) schemes are valid for outbound connections
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return SsrfValidationResult.Blocked(
                $"URL scheme '{uri.Scheme}' is not allowed. Only HTTP and HTTPS are permitted.");
        }

        var host = uri.Host;

        // Check additional blocked hosts first (cheapest comparison)
        if (additionalBlockedHosts is { Count: > 0 })
        {
            foreach (var blocked in additionalBlockedHosts)
            {
                if (host.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                {
                    return SsrfValidationResult.Blocked(
                        $"URL host '{host}' is blocked by configuration (SSRF prevention).");
                }
            }
        }

        // Blocked hostnames (exact, case-insensitive)
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return SsrfValidationResult.Blocked(
                $"URL host '{host}' is blocked for security reasons (SSRF prevention).");
        }

        // Try to parse as an IP address (handles both bare IPv4 and [IPv6] bracket notation)
        var hostToParse = host.StartsWith('[') && host.EndsWith(']')
            ? host[1..^1]   // strip IPv6 brackets
            : host;

        if (!IPAddress.TryParse(hostToParse, out var ip))
        {
            // Hostname — DNS resolution not performed at validation time.
            // Safe to proceed (DNS rebinding attacks require additional mitigations).
            return SsrfValidationResult.Allowed;
        }

        // IPv6 loopback ::1
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Loopback))
            {
                return SsrfValidationResult.Blocked(
                    $"URL host '{host}' is blocked for security reasons (SSRF prevention).");
            }

            // Other IPv6 addresses: allow (no private-range filtering beyond ::1)
            return SsrfValidationResult.Allowed;
        }

        // IPv4 range checks
        var bytes = ip.GetAddressBytes(); // big-endian: bytes[0] is most-significant
        var b0 = bytes[0];
        var b1 = bytes[1];

        bool isBlocked =
            b0 == 127 ||                                    // 127.0.0.0/8   loopback
            b0 == 0 ||                                      // 0.0.0.0/8     any
            b0 == 10 ||                                     // 10.0.0.0/8    RFC-1918
            (b0 == 169 && b1 == 254) ||                     // 169.254.0.0/16 link-local / IMDS
            (b0 == 172 && b1 >= 16 && b1 <= 31) ||         // 172.16.0.0/12 RFC-1918
            (b0 == 192 && b1 == 168) ||                     // 192.168.0.0/16 RFC-1918
            (b0 == 100 && (b1 & 0xC0) == 64);              // 100.64.0.0/10 CGN

        if (isBlocked)
        {
            return SsrfValidationResult.Blocked(
                $"URL host '{host}' is blocked for security reasons (SSRF prevention).");
        }

        return SsrfValidationResult.Allowed;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if the URI targets a blocked address.
    /// Convenience method for use in tool argument validation where an exception
    /// is the expected failure path.
    /// </summary>
    /// <param name="uri">The URI to validate.</param>
    /// <param name="additionalBlockedHosts">
    /// Optional additional hostnames to block (exact match, case-insensitive).
    /// </param>
    /// <exception cref="ArgumentException">Thrown when the URL is blocked.</exception>
    public static void AssertSafe(Uri uri, IReadOnlyList<string>? additionalBlockedHosts = null)
    {
        var result = Validate(uri, additionalBlockedHosts);
        if (!result.IsSafe)
        {
            throw new ArgumentException(result.Reason);
        }
    }
}

/// <summary>
/// Result of an SSRF validation check.
/// </summary>
public readonly struct SsrfValidationResult
{
    /// <summary>Whether the URL is safe to connect to.</summary>
    public bool IsSafe { get; }

    /// <summary>Human-readable reason the URL was blocked (null when safe).</summary>
    public string? Reason { get; }

    private SsrfValidationResult(bool isSafe, string? reason)
    {
        IsSafe = isSafe;
        Reason = reason;
    }

    /// <summary>The URL passed validation.</summary>
    public static SsrfValidationResult Allowed => new(true, null);

    /// <summary>The URL was blocked for the given reason.</summary>
    public static SsrfValidationResult Blocked(string reason) => new(false, reason);
}
