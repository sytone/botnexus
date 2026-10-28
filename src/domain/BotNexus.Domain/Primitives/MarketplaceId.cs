using System.Text.RegularExpressions;
using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a machine-global marketplace registration and its single configuration path segment.
/// Rejects rather than case-folding noncanonical input so callers cannot address a different key.
/// </summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct MarketplaceId
{
    private static Validation Validate(string value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 128 && IdentityPattern().IsMatch(value)
            ? Validation.Ok
            : Validation.Invalid("MarketplaceId must use lowercase alphanumeric segments separated by single hyphens (maximum 128 characters).");

    private static string NormalizeInput(string input) => input;

    [GeneratedRegex("\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();
}
