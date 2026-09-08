using System.Net;
using System.Net.Sockets;

namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Shared SSRF (Server-Side Request Forgery) validation utility.
/// Validates URLs against private network ranges, cloud metadata endpoints,
/// and configurable blocked hosts before opening outbound connections. URI validation is
/// lexical only; transports must enforce resolved destinations at connection establishment.
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
            // Lexical admission only. The transport must resolve, validate ALL candidates with
            // ValidateAddress, and bind each connection to an approved numerical address.
            return SsrfValidationResult.Allowed;
        }

        return ValidateAddress(ip, host);
    }

    /// <summary>
    /// Applies the same address policy used for URL literals to a resolved destination. A
    /// successful result is not DNS pinning: callers must connect to this numerical address,
    /// without resolving the hostname again, and reject mixed safe/blocked DNS answer sets.
    /// </summary>
    public static SsrfValidationResult ValidateAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return ValidateAddress(address, address.ToString());
    }

    private static SsrfValidationResult ValidateAddress(IPAddress ip, string host)
    {
        // IPv6 handling (#3809). An IPv4-mapped address such as [::ffff:169.254.169.254] is a
        // purely syntactic re-spelling of the IPv4 address it embeds, so it MUST be normalised
        // back to IPv4 and run through the same table below - never short-circuited as "some
        // IPv6 address". The previous early `return Allowed` here let every blocked IPv4 range
        // through in mapped form, including cloud metadata (IMDS) and the gateway's own loopback.
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IsLoopback (not Equals(IPv6Loopback)) so mapped loopback is caught too, matching
            // the sibling WebhookCallbackValidator.
            if (IPAddress.IsLoopback(ip) ||
                ip.Equals(IPAddress.IPv6Any) ||          // :: unspecified
                ip.IsIPv6LinkLocal ||                     // fe80::/10
                ip.IsIPv6UniqueLocal ||                   // fc00::/7
                ip.IsIPv6SiteLocal ||                     // fec0::/10 (deprecated)
                ip.IsIPv6Multicast)                       // ff00::/8
            {
                return SsrfValidationResult.Blocked(
                    $"URL host '{host}' is blocked for security reasons (SSRF prevention).");
            }

            if (ip.IsIPv4MappedToIPv6)
            {
                // Fall through to the IPv4 table with the embedded address. Keeping ONE table is
                // the #2745 intent; a parallel IPv6 copy is exactly the drift that caused this bug.
                ip = ip.MapToIPv4();
            }
            else if (TryExtractEmbeddedIPv4(ip, out var embedded))
            {
                // IPv4-compatible (::a.b.c.d) and 6to4 (2002:aabb:ccdd::/16) also carry an IPv4
                // address in their bits. IsIPv4MappedToIPv6 covers neither, so a 6to4-wrapped IMDS
                // address would still slip past. Classify by the address actually reached.
                ip = embedded;
            }
            else
            {
                // Genuine, routable IPv6 (e.g. 2606:4700:4700::1111): allowed. The blocks above
                // are deliberate ranges, not a blanket IPv6 ban.
                return SsrfValidationResult.Allowed;
            }
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
    /// Extracts the IPv4 address embedded in an IPv4-compatible (<c>::a.b.c.d</c>, RFC 4291) or
    /// 6to4 (<c>2002:aabb:ccdd::/16</c>, RFC 3056) IPv6 address. Both transitional forms reach the
    /// embedded IPv4 destination, so both must be classified by the IPv4 table (#3809);
    /// <see cref="IPAddress.IsIPv4MappedToIPv6"/> recognises neither.
    /// </summary>
    /// <param name="ip">An <see cref="AddressFamily.InterNetworkV6"/> address.</param>
    /// <param name="embedded">The embedded IPv4 address when one is present.</param>
    /// <returns><c>true</c> when an embedded IPv4 address was extracted.</returns>
    private static bool TryExtractEmbeddedIPv4(IPAddress ip, out IPAddress embedded)
    {
        embedded = IPAddress.None;
        var b = ip.GetAddressBytes();

        // 6to4: 2002:<32-bit IPv4>::/48
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            embedded = new IPAddress(new[] { b[2], b[3], b[4], b[5] });
            return true;
        }

        // IPv4-compatible: 96 zero bits followed by the IPv4 address. ::  and ::1 are excluded -
        // they are handled as unspecified/loopback above and are not embedded-IPv4 carriers.
        for (var i = 0; i < 12; i++)
        {
            if (b[i] != 0)
                return false;
        }

        if (b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1)
            return false;

        embedded = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
        return true;
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
    /// <summary>Whether the supplied URI text or numerical address passed policy; not proof of DNS pinning.</summary>
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
