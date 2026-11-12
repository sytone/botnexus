using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Imports only the legacy root manifest's conventional skills. Versioned schema dialects and
/// foreign execution components are deliberately not interpreted. The caller preflights the tree.
/// </summary>
internal sealed class LegacyPluginImportAdapter(PluginManifestParser parser)
{
    private const string PortablePath = "plugin.json";
    private const string NestedEnhancement = ".botnexus-plugin/plugin.json";
    private const string RootEnhancement = "botnexus-plugin.json";
    private static readonly HashSet<string> MetadataFields = new(StringComparer.Ordinal)
    {
        "name", "description", "version", "author", "homepage", "repository", "license", "keywords",
    };

    public PluginParseResult<ImportedPlugin> ParseDirectory(string directory, bool marketplaceSelected = false)
    {
        var errors = new List<PluginValidationError>();
        var skipped = new HashSet<string>(PluginComponentPolicy.Discover(directory), StringComparer.Ordinal);
        // Claude metadata remains a conflicting envelope, not an opaque component payload.
        var claude = Path.Combine(directory, ".claude-plugin");
        var catalogOnly = marketplaceSelected && Directory.Exists(claude)
            && Directory.GetFileSystemEntries(claude) is { Length: 1 } entries
            && Path.GetFileName(entries[0]) == "marketplace.json" && File.Exists(entries[0]);
        if ((File.Exists(claude) || Directory.Exists(claude)) && !catalogOnly)
            errors.Add(new(".claude-plugin", "Legacy import does not accept a second foreign metadata envelope."));

        var baseline = ReadObject(directory, PortablePath, errors);
        var projected = baseline is null ? new JsonObject() : Project(baseline, PortablePath, false, errors, skipped);
        // Validate baseline independently: enhancement cannot repair an invalid portable identity/metadata.
        var portable = parser.ParseManifest(projected.ToJsonString());
        errors.AddRange(portable.Errors);

        // Component-only metadata is not an enhancement manifest. A present manifest entry,
        // including a directory/alias, remains authoritative and cannot fall back to the root.
        var nestedDirectory = Path.Combine(directory, PluginManifestParser.PluginMetadataDirectoryName);
        if (File.Exists(nestedDirectory))
            errors.Add(new(PluginManifestParser.PluginMetadataDirectoryName, "Plugin metadata must be a directory."));
        var hasNestedManifest = Directory.Exists(nestedDirectory)
            && Directory.EnumerateFileSystemEntries(nestedDirectory).Any(path =>
                Path.GetFileName(path).Equals(PluginManifestParser.ManifestFileName, StringComparison.OrdinalIgnoreCase));
        var selected = hasNestedManifest ? NestedEnhancement
            : Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) == RootEnhancement)
                ? RootEnhancement : null;
        if (selected is not null)
        {
            var enhancement = ReadObject(directory, selected, errors);
            if (enhancement is not null)
                Merge(projected, Project(enhancement, selected, true, errors, skipped), "", selected, errors);
        }

        var merged = parser.ParseManifest(projected.ToJsonString());
        errors.AddRange(merged.Errors);
        if (errors.Count > 0)
            return PluginParseResult<ImportedPlugin>.Failure(errors);
        return merged.Value is { } manifest
            ? PluginParseResult<ImportedPlugin>.Success(new(manifest, PluginManifestFormat.Legacy, true, PluginComponentPolicy.Normalize(skipped)))
            : PluginParseResult<ImportedPlugin>.Failure(PortablePath, "Legacy manifest could not be imported.");
    }

    private static JsonObject? ReadObject(string directory, string relative, List<PluginValidationError> errors)
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            errors.Add(new(relative, $"Present metadata requires a readable canonical '{relative}'."));
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var beforeDuplicates = errors.Count;
            PluginImportAdapter.CheckDuplicates(document.RootElement, relative, errors);
            if (errors.Count != beforeDuplicates) return null;
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
            errors.Add(new(relative, $"Manifest '{relative}' must be an object."));
        }
        catch (JsonException ex)
        {
            errors.Add(new(relative, $"Manifest '{relative}' is not valid JSON: {ex.Message}"));
        }
        return null;
    }

    private static JsonObject Project(JsonObject source, string path, bool enhancement, List<PluginValidationError> errors, HashSet<string> skipped)
    {
        var result = new JsonObject();
        foreach (var (field, value) in source)
        {
            if (MetadataFields.Contains(field))
                result[field] = value?.DeepClone();
            else if (field == "skills")
            {
                // Native enhancement retains the native array shape, but cannot narrow or redirect skills.
                if (!IsConventionalSkills(value, enhancement))
                    errors.Add(new(field, $"'{path}' skills must name only the conventional skills directory."));
            }
            else if (field == "displayName" && !enhancement)
            {
                // Display-only portable metadata has no native equivalent; retain the source verbatim on disk.
                if (value is not JsonValue display || !display.TryGetValue<string>(out _))
                    errors.Add(new(field, $"'{path}' displayName must be a string."));
            }
            else if (PluginComponentPolicy.CategoryForField(field) is { } category)
                skipped.Add(category);
            else if (field is "$schema" or "schemaVersion")
                errors.Add(new(field, $"'{path}' opts into a schema dialect unsupported by legacy skills-only import."));
            else
                errors.Add(new(field, $"Legacy skills-only import does not support '{field}' in '{path}'."));
        }
        return result;
    }

    private static bool IsConventionalSkills(JsonNode? value, bool enhancement)
    {
        if (value is JsonArray array)
            return array.Count == 1 && IsConventionalPath(array[0]);
        return !enhancement && IsConventionalPath(value);
    }

    private static bool IsConventionalPath(JsonNode? value) => value is JsonValue scalar
        && scalar.TryGetValue<string>(out var path) && path is "skills" or "./skills" or "skills/" or "./skills/";

    private static void Merge(JsonObject baseline, JsonObject enhancement, string prefix, string source,
        List<PluginValidationError> errors)
    {
        foreach (var (field, value) in enhancement)
        {
            var path = prefix.Length == 0 ? field : prefix + "/" + field;
            if (!baseline.TryGetPropertyValue(field, out var existing))
                baseline[field] = value?.DeepClone();
            else if (existing is JsonObject existingObject && value is JsonObject addition)
                Merge(existingObject, addition, path, source, errors);
            else if (!JsonNode.DeepEquals(existing, value))
                errors.Add(new(path, $"Conflicting '{path}' declarations in '{PortablePath}' and '{source}'."));
        }
    }
}
