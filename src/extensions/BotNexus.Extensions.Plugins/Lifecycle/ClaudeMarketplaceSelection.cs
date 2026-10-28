using System.Text.Json;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Explicit Claude catalog boundary. Paths are repository-root relative, never metadata-relative.
/// This does not participate in native marketplace parsing or fetch additional sources.
/// </summary>
internal static class ClaudeMarketplaceSelection
{
    private const int MaxDocumentBytes = 1024 * 1024;
    private const string CatalogPath = ".claude-plugin/marketplace.json";
    private const string CopilotCatalogPath = ".github/plugin/marketplace.json";
    private static readonly HashSet<string> EntryMetadata = new(StringComparer.Ordinal)
    {
        "name", "description", "author", "homepage", "repository", "license", "keywords",
    };

    internal static PluginParseResult<Selection> Select(string directory, string name, PluginManifestParser parser)
    {
        var errors = new List<PluginValidationError>();
        var identity = parser.ParseManifest(JsonSerializer.Serialize(new { name }));
        errors.AddRange(identity.Errors);
        // Inspect before opening metadata or traversing a selected path. Deliberately conservative:
        // the fetched source must be link-free, including unselected siblings and dangling links.
        errors.AddRange(PluginImportAdapter.ValidateImportTree(directory));
        if (errors.Count > 0) return PluginParseResult<Selection>.Failure(errors);
        var catalogPath = CatalogPath;
        try
        {
            var candidates = new[] { CatalogPath, CopilotCatalogPath }
                .Where(candidate => HasCatalogEntry(directory, candidate, errors)).ToArray();
            if (candidates.Length > 1)
                errors.Add(new("marketplacePluginName", "Multiple supported marketplace catalogs are ambiguous; selection requires exactly one dialect."));
            if (errors.Count > 0) return PluginParseResult<Selection>.Failure(errors);
            if (candidates.Length == 1) catalogPath = candidates[0];
            var copilot = catalogPath == CopilotCatalogPath;
            var path = Path.Combine(directory, catalogPath.Replace('/', Path.DirectorySeparatorChar));
            using var stream = File.OpenRead(path);
            var bytes = new byte[MaxDocumentBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxDocumentBytes)
                return PluginParseResult<Selection>.Failure(catalogPath, "Marketplace document exceeds 1 MiB.");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            var root = document.RootElement;
            CheckDuplicates(root, catalogPath, errors);
            if (errors.Count > 0) return PluginParseResult<Selection>.Failure(errors);
            if (root.ValueKind != JsonValueKind.Object)
                return PluginParseResult<Selection>.Failure(catalogPath, "Marketplace must be an object.");
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("name" or "owner" or "metadata" or "plugins" or "$schema"))
                    errors.Add(new(property.Name, "Unsupported Claude marketplace field."));
            }
            if (!root.TryGetProperty("name", out var marketName) || marketName.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(marketName.GetString()))
                errors.Add(new("name", "Claude marketplace requires a name."));
            if (!root.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object)
                errors.Add(new("owner", "Claude marketplace requires an owner object."));
            else if (!owner.TryGetProperty("name", out var ownerName) || ownerName.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(ownerName.GetString()))
                errors.Add(new("owner/name", "Claude marketplace owner requires a nonempty name."));
            ValidateDescriptiveObject(root, "owner", ["name", "email", "url"], errors);
            ValidateDescriptiveObject(root, "metadata", ["description", "version"], errors);
            if (root.TryGetProperty("$schema", out var schema) && schema.ValueKind != JsonValueKind.String)
                errors.Add(new("$schema", "Claude marketplace schema identifier must be a string; it is never fetched."));
            if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                return PluginParseResult<Selection>.Failure(catalogPath, "Marketplace requires a plugins array.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? selected = null;
            foreach (var entry in plugins.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new("plugins", "Claude marketplace entries must be objects."));
                    continue;
                }
                var entryName = entry.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                // Unselected identities belong to the foreign catalog, not the native schema.
                // Keep catalog structure and ambiguity checks without importing their name rules.
                if (string.IsNullOrWhiteSpace(entryName))
                    errors.Add(new("#/name", "Claude marketplace entry requires a nonempty string identity."));
                if (entryName is not null && !names.Add(entryName))
                    errors.Add(new("name", "Duplicate Claude marketplace plugin identity."));
                if (!entry.TryGetProperty("source", out var source))
                {
                    errors.Add(new("sourcePath", "Claude marketplace entry requires a source."));
                    continue;
                }
                // Only the explicitly selected entry is imported. Other entries may advertise
                // unsupported source types/components without activating or fetching them.
                if (!string.Equals(name, entryName, StringComparison.Ordinal)) continue;
                if (source.ValueKind != JsonValueKind.String || !IsSafeRelativeSource(source.GetString()))
                    errors.Add(new("sourcePath", "Selected Claude source must be './' or a canonical contained './subdirectory' path."));
                else selected = source.GetString();
                foreach (var property in entry.EnumerateObject())
                {
                    if (property.Name == "source") continue;
                    if (!EntryMetadata.Contains(property.Name) && !(copilot && property.Name == "version"))
                        errors.Add(new(property.Name, "Unsupported selected Claude entry component, override or field."));
                }
                // Metadata is descriptive only; validate supported types with the unchanged native schema.
                var metadata = entry.EnumerateObject().Where(p => EntryMetadata.Contains(p.Name) || (copilot && p.Name == "version"))
                    .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                errors.AddRange(parser.ParseManifest(JsonSerializer.Serialize(metadata)).Errors);
            }
            if (selected is null) errors.Add(new("marketplacePluginName", "No supported entry matches the explicit Claude plugin identity."));
            if (errors.Count > 0) return PluginParseResult<Selection>.Failure(errors);
            var selectedDirectory = selected == "./" ? directory : Path.Combine(directory, selected![2..].Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(selectedDirectory))
                return PluginParseResult<Selection>.Failure("sourcePath", "Selected Claude source directory does not exist.");
            return PluginParseResult<Selection>.Success(new Selection(selectedDirectory, catalogPath, selected!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return PluginParseResult<Selection>.Failure(catalogPath, $"Could not read marketplace: {ex.Message}");
        }
    }

    // Inspect names segment-by-segment so Windows cannot accept aliases that Linux would miss.
    private static bool HasCatalogEntry(string directory, string relative, List<PluginValidationError> errors)
    {
        var current = directory;
        foreach (var segment in relative.Split('/'))
        {
            if (!System.IO.Directory.Exists(current)) return false;
            var entry = System.IO.Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;
            if (Path.GetFileName(entry) != segment)
                errors.Add(new(relative, "Marketplace catalog path requires canonical spelling."));
            current = entry;
        }
        return true;
    }

    private static void ValidateDescriptiveObject(JsonElement root, string field, string[] allowed, List<PluginValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value)) return;
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new(field, "Claude marketplace descriptive metadata must be an object."));
            return;
        }
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || property.Value.ValueKind != JsonValueKind.String)
                errors.Add(new(field + "/" + property.Name, "Unsupported Claude marketplace metadata field or type."));
    }

    private static bool IsSafeRelativeSource(string? source) => source == "./" ||
        (source is not null && source.StartsWith("./", StringComparison.Ordinal)
        && source[2..].Split('/').All(part => part is not ("" or "." or "..")
            && !part.Contains('%') && !part.Contains('#') && PluginImportAdapter.IsPortableName(part)));

    private static void CheckDuplicates(JsonElement value, string path, List<PluginValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!fields.Add(property.Name)) errors.Add(new(path, $"Duplicate field '{property.Name}'."));
                CheckDuplicates(property.Value, path + "/" + property.Name, errors);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item, path, errors);
    }

    internal sealed record Selection(string Directory, string CatalogPath, string SourcePath);
}
