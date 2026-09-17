namespace BotNexus.Gateway.Configuration;

/// <summary>Shared rules for gateway credentials supplied through platform configuration.</summary>
public static class GatewayApiKeyRules
{
    /// <summary>
    /// Stable fragment included in validation errors for explicit placeholder credentials.
    /// Authentication uses it to distinguish a credential failure from an unrelated invalid
    /// hot-reload and fail closed rather than retaining the previous credential snapshot.
    /// </summary>
    public const string PlaceholderValidationMarker = "must not be a placeholder.";

    /// <summary>
    /// Returns whether a configured credential is an explicit non-secret placeholder.
    /// Matching trims surrounding whitespace and compares the complete value using
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. Blank values are not placeholders;
    /// their existing required/intentional-keyless semantics remain unchanged.
    /// </summary>
    public static bool IsExplicitPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        return candidate.Equals("undefined", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("null", StringComparison.OrdinalIgnoreCase);
    }
}
