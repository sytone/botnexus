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

    public PluginParseResult<ImportedPlugin> ParseDirectory(string directory)
    {
        var errors = PluginImportAdapter.ValidateDiscoveredComponents(directory);
        foreach (var relative in new[] { "mcp.json", "com.github.copilot", ".claude-plugin" })
        {
            var path = Path.Combine(directory, relative);
            if (File.Exists(path) || Directory.Exists(path))
                errors.Add(new(relative, $"Legacy skills-only import does not support discovered path '{relative}'."));
        }

        var baseline = ReadObject(directory, PortablePath, errors);
        var projected = baseline is null ? new JsonObject() : Project(baseline, PortablePath, false, errors);
        // Validate baseline independently: enhancement cannot repair an invalid portable identity/metadata.
        var portable = parser.ParseManifest(projected.ToJsonString());
        errors.AddRange(portable.Errors);

        // A present nested metadata entry is authoritative even if it is a malformed directory/file.
        // Never fall back from a selected, unreadable nested enhancement to the root enhancement.
        var selected = Directory.EnumerateFileSystemEntries(directory).Any(path =>
            Path.GetFileName(path) == PluginManifestParser.PluginMetadataDirectoryName)
                ? NestedEnhancement
                : Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) == RootEnhancement)
                    ? RootEnhancement : null;
        if (selected is not null)
        {
            var enhancement = ReadObject(directory, selected, errors);
            if (enhancement is not null)
                Merge(projected, Project(enhancement, selected, true, errors), "", selected, errors);
        }

        var merged = parser.ParseManifest(projected.ToJsonString());
        errors.AddRange(merged.Errors);
        if (errors.Count > 0)
            return PluginParseResult<ImportedPlugin>.Failure(errors);
        return merged.Value is { } manifest
            ? PluginParseResult<ImportedPlugin>.Success(new(manifest, PluginManifestFormat.Legacy))
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

    private static JsonObject Project(JsonObject source, string path, bool enhancement, List<PluginValidationError> errors)
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
