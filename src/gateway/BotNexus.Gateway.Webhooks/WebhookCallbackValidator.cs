using BotNexus.Gateway.Abstractions.Security;

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

        // Keep callback admission on the shared SSRF policy. The explicit null is intentional:
        // architecture checks require call sites to acknowledge the optional blocked-host policy.
        var validation = SsrfValidator.Validate(uri, additionalBlockedHosts: null);
        return validation.IsSafe
            ? CallbackValidationResult.Safe()
            : CallbackValidationResult.Rejected(validation.Reason ?? "Callback URL was rejected by SSRF prevention.");
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
