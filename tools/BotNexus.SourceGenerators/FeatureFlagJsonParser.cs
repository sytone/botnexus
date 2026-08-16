namespace BotNexus.SourceGenerators;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// Parses <c>feature-flags.json</c> into <see cref="FeatureFlagDefinitionModel"/> entries at
/// compile time.
/// <para>
/// <b>Every failure throws.</b> The generator turns the exception into a build error with a
/// diagnostic ID rather than emitting an empty inventory, because silently generating nothing is
/// indistinguishable from "there are no flags" - and the resulting cascade of
/// <c>FeatureFlags does not exist</c> errors points at the call sites instead of the malformed
/// file that caused them. This is a deliberate divergence from the Oro reference generator, which
/// swallows parse errors to avoid crashing the compiler host; a build error is not a crash, and
/// the inventory is small enough that failing loudly costs nothing.
/// </para>
/// <para>
/// Runs inside the Roslyn compiler process on every keystroke when the incremental cache is
/// invalidated, so it stays allocation-conscious and does no I/O of its own.
/// </para>
/// </summary>
public static class FeatureFlagJsonParser
{
    /// <summary>
    /// Parses the <c>{ "flags": [...] }</c> document.
    /// </summary>
    /// <param name="jsonContent">Full contents of a <c>feature-flags.json</c> file.</param>
    /// <returns>One model per entry in the <c>flags</c> array, in file order.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the content is empty, is not valid JSON, is missing the <c>flags</c> array,
    /// omits a required property (<c>featureName</c>, <c>description</c>, <c>owner</c>,
    /// <c>dateAdded</c>, <c>defaultState</c>), carries a date that is not <c>yyyy-MM-dd</c>, or
    /// declares the same feature name twice. The message names the offending flag wherever the
    /// flag is identifiable, because "invalid JSON" alone does not tell an author which of
    /// thirty entries to fix.
    /// </exception>
    public static List<FeatureFlagDefinitionModel> ParseJson(string jsonContent)
    {
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            throw new ArgumentException("feature-flags.json is empty.", nameof(jsonContent));
        }

        try
        {
            using var document = JsonDocument.Parse(jsonContent);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "feature-flags.json must be a JSON object with a 'flags' array property.");
            }

            if (!root.TryGetProperty("flags", out var flagsArray) ||
                flagsArray.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("feature-flags.json must contain a 'flags' array property.");
            }

            var flags = new List<FeatureFlagDefinitionModel>();
            foreach (var element in flagsArray.EnumerateArray())
            {
                flags.Add(ParseFlagElement(element));
            }

            EnsureFeatureNamesAreUnique(flags);
            return flags;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"feature-flags.json is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Rejects duplicate feature names case-insensitively. Case-insensitive because the generated
    /// member names would collide on case anyway; catching it here names the flag instead of
    /// emitting a duplicate-member error in generated source the author cannot edit. This is also
    /// the shape a bad merge takes when the same flag is added on both sides.
    /// </summary>
    private static void EnsureFeatureNamesAreUnique(List<FeatureFlagDefinitionModel> flags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();

        foreach (var flag in flags)
        {
            if (!seen.Add(flag.FeatureName))
            {
                duplicates.Add(flag.FeatureName);
            }
        }

        if (duplicates.Count > 0)
        {
            throw new ArgumentException(
                "Duplicate feature flag name(s) in feature-flags.json: "
                + string.Join(", ", duplicates)
                + ". Feature names must be unique (case-insensitive).");
        }
    }

    private static FeatureFlagDefinitionModel ParseFlagElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Each entry in 'flags' must be a JSON object.");
        }

        var featureName = GetRequiredString(element, "featureName", flagName: null);
        var description = GetRequiredString(element, "description", featureName);
        var owner = GetRequiredString(element, "owner", featureName);
        var dateAdded = ParseDate(GetRequiredString(element, "dateAdded", featureName), "dateAdded", featureName);

        DateTime? dateRetired = null;
        if (element.TryGetProperty("dateRetired", out var retiredProperty) &&
            retiredProperty.ValueKind == JsonValueKind.String)
        {
            var raw = retiredProperty.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                dateRetired = ParseDate(raw, "dateRetired", featureName);
            }
        }

        return new FeatureFlagDefinitionModel
        {
            FeatureName = featureName,
            Description = description,
            Owner = owner,
            DateAdded = dateAdded,
            DefaultState = GetRequiredBool(element, "defaultState", featureName),
            DateRetired = dateRetired,
            IgnoreFlagAge = GetOptionalBool(element, "ignoreFlagAge"),
        };
    }

    private static string GetRequiredString(JsonElement element, string propertyName, string flagName)
    {
        var subject = flagName is null ? string.Empty : $" on flag '{flagName}'";

        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Required property '{propertyName}'{subject} is missing.");
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Required property '{propertyName}'{subject} cannot be empty.");
        }

        return value;
    }

    private static bool GetRequiredBool(JsonElement element, string propertyName, string flagName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new ArgumentException($"Required property '{propertyName}' on flag '{flagName}' is missing.");
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        throw new ArgumentException(
            $"Property '{propertyName}' on flag '{flagName}' must be a boolean.");
    }

    private static bool GetOptionalBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static DateTime ParseDate(string raw, string propertyName, string flagName)
    {
        if (DateTime.TryParseExact(
                raw,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"Invalid date format for '{propertyName}' on flag '{flagName}'. Expected 'yyyy-MM-dd', got '{raw}'.");
    }
}
