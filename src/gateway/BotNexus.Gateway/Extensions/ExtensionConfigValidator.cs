using System.Text.Json;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// Validates an extension's runtime config against its declared schema.
/// Produces warnings for missing required fields and applies defaults for optional absent fields.
/// </summary>
public sealed class ExtensionConfigValidator
{
    /// <summary>
    /// Validates <paramref name="config"/> against <paramref name="schema"/>.
    /// </summary>
    /// <param name="extensionId">Extension ID, used in warning messages.</param>
    /// <param name="schema">Schema declared in the extension manifest.</param>
    /// <param name="config">Operator-supplied config element for this extension.</param>
    public ExtensionConfigValidationResult Validate(
        string extensionId,
        IReadOnlyList<ExtensionConfigFieldSchema> schema,
        JsonElement config)
    {
        var warnings = new List<string>();
        var appliedDefaults = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in schema)
        {
            var hasValue = config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty(field.Id, out _);

            if (hasValue)
                continue;

            if (field.Required)
            {
                warnings.Add(
                    $"Extension '{extensionId}': required config field '{field.Id}' is missing.");
            }
            else if (field.Default is not null)
            {
                appliedDefaults[field.Id] = field.Default;
            }
        }

        return new ExtensionConfigValidationResult(
            IsValid: warnings.Count == 0,
            Warnings: warnings,
            AppliedDefaults: appliedDefaults);
    }
}

/// <summary>
/// Result of validating extension config against its declared schema.
/// </summary>
/// <param name="IsValid">True when no required fields are missing.</param>
/// <param name="Warnings">Human-readable warning messages (one per missing required field).</param>
/// <param name="AppliedDefaults">Key-value pairs for fields where the schema default was applied.</param>
public sealed record ExtensionConfigValidationResult(
    bool IsValid,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string> AppliedDefaults);
