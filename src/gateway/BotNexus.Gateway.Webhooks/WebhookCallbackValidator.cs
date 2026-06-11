using System.Net;
using System.Net.Sockets;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Validates webhook callback URLs against SSRF attack vectors.
/// Prevents the gateway from being used as a proxy to reach internal services
/// via attacker-controlled callback URLs.
/// </summary>
public static class WebhookCallbackValidator
{
    /// <summary>
    /// Validates that a callback URL does not target private, loopback, link-local,
    /// or cloud metadata network addresses.
    /// </summary>
    /// <param name="url">The callback URL to validate.</param>
    /// <returns>A result indicating whether the URL is safe for outbound delivery.</returns>
    public static CallbackValidationResult IsCallbackUrlSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return CallbackValidationResult.Rejected("Callback URL is null or empty.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return CallbackValidationResult.Rejected($"Callback URL '{url}' is not a valid absolute URI.");

        // Only HTTP(S) schemes are allowed
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return CallbackValidationResult.Rejected(
                $"Callback URL scheme '{uri.Scheme}' is not allowed. Only HTTP and HTTPS are permitted.");

        var host = uri.Host;

        // Block well-known dangerous hostnames
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return CallbackValidationResult.Rejected(
                $"Callback URL host '{host}' is blocked (SSRF prevention).");
        }

        // Parse IP addresses and check ranges
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IsBlockedAddress(ip))
            {
                return CallbackValidationResult.Rejected(
                    $"Callback URL targets a private/reserved IP address '{host}' (SSRF prevention).");
            }
        }

        // Check for IPv6 bracket notation ([::1])
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            var innerIp = host[1..^1];
            if (IPAddress.TryParse(innerIp, out var bracketIp) && IsBlockedAddress(bracketIp))
            {
                return CallbackValidationResult.Rejected(
                    $"Callback URL targets a private/reserved IP address '{host}' (SSRF prevention).");
            }
        }

        return CallbackValidationResult.Safe();
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        // Loopback (127.0.0.0/8, ::1)
        if (IPAddress.IsLoopback(address))
            return true;

        // IPv6 link-local (fe80::/10)
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal)
            return true;

        // IPv4 checks
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            // 169.254.0.0/16 (link-local / cloud metadata)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;

            // 0.0.0.0/8
            if (bytes[0] == 0)
                return true;
        }

        return false;
    }
}

/// <summary>
/// Result of a webhook callback URL validation check.
/// </summary>
public readonly record struct CallbackValidationResult
{
    /// <summary>Whether the URL is safe for outbound delivery.</summary>
    public bool IsSafe { get; init; }

    /// <summary>Reason the URL was rejected. Null when safe.</summary>
    public string? Reason { get; init; }

    /// <summary>Creates a safe result.</summary>
    public static CallbackValidationResult Safe() => new() { IsSafe = true, Reason = null };

    /// <summary>Creates a rejected result with reason.</summary>
    public static CallbackValidationResult Rejected(string reason) => new() { IsSafe = false, Reason = reason };
}
