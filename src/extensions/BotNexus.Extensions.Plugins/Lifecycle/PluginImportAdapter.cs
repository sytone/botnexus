using System.Text;
using System.Text.Json;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Adapts only the conventional-skills subset of Claude plugins at the staging boundary.
/// Native parsing and its schema remain independent of this deliberately bounded contract.
/// </summary>
internal sealed class PluginImportAdapter(PluginManifestParser parser)
{
    private const string ClaudeManifestPath = ".claude-plugin/plugin.json";
    private static readonly HashSet<string> MetadataFields = new(StringComparer.Ordinal)
    {
        "name", "description", "version", "author", "homepage", "repository", "license", "keywords",
    };
    private static readonly HashSet<string> UnsupportedFields = new(StringComparer.Ordinal)
    {
        "hooks", "commands", "agents", "mcpServers", "lspServers",
    };

    public PluginParseResult<ImportedPlugin> ParseDirectory(string directory, string? expectedName = null)
    {
        // Inspect entry names, not File.Exists: a dangling metadata link still marks a
        // Claude input. Do not parse either manifest until the entire tree passes preflight.
        var hasClaudeInput = Directory.EnumerateFileSystemEntries(directory).Any(path =>
        {
            var name = Path.GetFileName(path);
            return name.Equals(".claude-plugin", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(@".claude-plugin\", StringComparison.OrdinalIgnoreCase);
        });
        var nativePath = Path.Combine(directory, PluginManifestParser.PluginMetadataDirectoryName,
            PluginManifestParser.ManifestFileName);
        // Native-only inputs retain their existing contract. Every potential foreign or
        // manifestless input must pass preflight, including dangling native metadata links.
        var nativeMetadata = Path.Combine(directory, PluginManifestParser.PluginMetadataDirectoryName);
        var linkedNativeMetadata = (Directory.Exists(nativeMetadata)
                && (File.GetAttributes(nativeMetadata) & FileAttributes.ReparsePoint) != 0)
            || (File.Exists(nativePath) && (File.GetAttributes(nativePath) & FileAttributes.ReparsePoint) != 0);
        if (hasClaudeInput || !File.Exists(nativePath) || linkedNativeMetadata)
        {
            var safetyErrors = ValidateImportTree(directory);
            if (safetyErrors.Count > 0)
                return PluginParseResult<ImportedPlugin>.Failure(safetyErrors);
        }

        var claudePath = Path.Combine(directory, ".claude-plugin", PluginManifestParser.ManifestFileName);
        var hasNative = File.Exists(nativePath);
        var native = hasNative ? parser.ParsePluginDirectory(directory) : null;
        // A present native manifest is authoritative, including when it is invalid.
        if (native is { IsValid: false })
            return PluginParseResult<ImportedPlugin>.Failure(native.Errors);
        if (!File.Exists(claudePath))
        {
            if (native?.Value is { } nativeOnly)
                return PluginParseResult<ImportedPlugin>.Success(new(nativeOnly, PluginManifestFormat.Native));
            // A metadata entry that is present but cannot be read is not permission to
            // reinterpret the input as manifestless (including directory/file aliases).
            if (hasClaudeInput || File.Exists(nativeMetadata) || Directory.Exists(nativePath)
                || HasManifestEntry(directory, PluginManifestParser.PluginMetadataDirectoryName))
                return PluginParseResult<ImportedPlugin>.Failure(ClaudeManifestPath,
                    "Present plugin metadata does not contain a readable canonical manifest.");
            return ParseManifestless(directory, expectedName);
        }

        var errors = new List<PluginValidationError>();
        if (native?.Value is { } dualNative)
        {
            // Native wins as a whole, but cannot silently introduce behavior outside this
            // import stage when a Claude manifest is also present. Native-only is unchanged.
            foreach (var (field, declared) in new[]
            {
                ("hooks", dualNative.Hooks is not null),
                ("commands", dualNative.Commands is not null),
                ("agents", dualNative.Agents is not null),
                ("mcpServers", dualNative.McpServers is not null),
            })
            {
                if (declared)
                    errors.Add(new(field, $"Dual-manifest skills-only import does not support native '{field}'."));
            }
            if (dualNative.Skills is { } skills
                && (skills.Count != 1 || skills[0] is not ("./skills" or "skills")))
                errors.Add(new("skills", "Dual-manifest import requires native skills to name only the conventional './skills' directory."));
        }
        // These conventional paths can be discovered independently of manifest declarations.
        errors.AddRange(ValidateDiscoveredComponents(directory));

        PluginManifest? claudeManifest = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(claudePath));
            var root = document.RootElement;
            CheckDuplicates(root, ClaudeManifestPath, errors);
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(ClaudeManifestPath, "Claude plugin manifest must be an object."));
            }
            else
            {
                // Project metadata only; the unchanged native schema validates its types and identity.
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (var property in root.EnumerateObject())
                    {
                        if (MetadataFields.Contains(property.Name))
                            property.WriteTo(writer);
                        else if (property.Name == "skills")
                        {
                            if (!IsConventionalSkills(property.Value))
                                errors.Add(new("skills", "Claude skills must name only the conventional './skills' directory; custom or empty declarations are unsupported."));
                        }
                        else if (UnsupportedFields.Contains(property.Name))
                            errors.Add(new(property.Name, $"Claude skills-only import does not support '{property.Name}'."));
                        else
                            errors.Add(new(property.Name, $"Unknown Claude plugin field '{property.Name}'."));
                    }
                    writer.WriteEndObject();
                }
                var metadata = parser.ParseManifest(Encoding.UTF8.GetString(buffer.ToArray()));
                errors.AddRange(metadata.Errors);
                claudeManifest = metadata.Value;
            }
        }
        catch (JsonException ex)
        {
            errors.Add(new(ClaudeManifestPath, $"Claude plugin manifest is not valid JSON: {ex.Message}"));
        }

        if (native?.Value is { } nativeManifest && claudeManifest is not null
            && !string.Equals(nativeManifest.Name, claudeManifest.Name, StringComparison.Ordinal))
            errors.Add(new("name", "Native and Claude plugin manifests declare different identities."));

        if (errors.Count > 0)
            return PluginParseResult<ImportedPlugin>.Failure(errors);
        if (native?.Value is { } authoritative)
            return PluginParseResult<ImportedPlugin>.Success(new(authoritative, PluginManifestFormat.Native));
        if (claudeManifest is not null)
            return PluginParseResult<ImportedPlugin>.Success(new(claudeManifest, PluginManifestFormat.Claude));
        return PluginParseResult<ImportedPlugin>.Failure(ClaudeManifestPath, "Claude plugin manifest could not be imported.");
    }

    private PluginParseResult<ImportedPlugin> ParseManifestless(string directory, string? expectedName)
    {
        // Use the native schema for identity validation, never a source/directory-derived name.
        var metadata = parser.ParseManifest(JsonSerializer.Serialize(new { name = expectedName }));
        var errors = new List<PluginValidationError>(metadata.Errors);
        errors.AddRange(ValidateDiscoveredComponents(directory));
        var skills = Path.Combine(directory, "skills");
        var foundSkill = false;
        if (Directory.Exists(skills))
        {
            foreach (var child in Directory.EnumerateDirectories(skills))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(child))
                {
                    var name = Path.GetFileName(entry);
                    if (!name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
                    var field = Path.GetRelativePath(directory, entry).Replace(Path.DirectorySeparatorChar, '/');
                    if (!name.Equals("SKILL.md", StringComparison.Ordinal) || !File.Exists(entry))
                        errors.Add(new(field, "Manifestless import requires a canonical regular SKILL.md file."));
                    else
                        foundSkill = true;
                }
            }
        }
        if (!foundSkill)
            errors.Add(new("skills", "Manifestless import requires at least one canonical skills/<skill>/SKILL.md file."));
        if (errors.Count > 0)
            return PluginParseResult<ImportedPlugin>.Failure(errors);
        if (metadata.Value is { } manifest)
            return PluginParseResult<ImportedPlugin>.Success(new(manifest, PluginManifestFormat.Claude));
        return PluginParseResult<ImportedPlugin>.Failure("name", "Manifestless import requires an explicit valid plugin name.");
    }

    private static bool HasManifestEntry(string directory, string metadataDirectory)
    {
        var path = Path.Combine(directory, metadataDirectory);
        return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path)
            .Any(entry => Path.GetFileName(entry).Equals(PluginManifestParser.ManifestFileName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<PluginValidationError> ValidateDiscoveredComponents(string directory)
    {
        var errors = new List<PluginValidationError>();
        foreach (var relative in new[] { "hooks", "commands", "agents", "lsp", ".mcp.json", ".lsp.json", ".botnexus-plugin/mcp.json" })
        {
            var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path) || File.Exists(path))
                errors.Add(new(relative, $"Claude skills-only import does not support discovered path '{relative}'."));
        }
        return errors;
    }

    private static List<PluginValidationError> ValidateImportTree(string directory)
    {
        var errors = new List<PluginValidationError>();
        var root = Path.GetFullPath(directory);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                errors.Add(new("directory", "Claude import staging root must not be a symbolic link or reparse point."));
                return errors;
            }
            while (pending.TryPop(out var current))
            {
                // Never use recursive enumeration: inspect each entry before deciding to descend.
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var relative = Path.GetRelativePath(root, entry);
                    var field = relative.Replace(Path.DirectorySeparatorChar, '/');
                    var name = Path.GetFileName(entry);
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        errors.Add(new(field, "Claude import does not support symbolic links or reparse points."));
                        continue;
                    }
                    if (!IsPortableName(name) || Path.IsPathRooted(relative)
                        || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        errors.Add(new(field, "Claude import path is not an unambiguous, contained cross-platform destination."));
                        continue;
                    }
                    if (!destinations.Add(field))
                        errors.Add(new(field, "Claude import paths collide under case-insensitive destination comparison."));

                    // Runtime convention discovery can be case-insensitive on Windows. Reject
                    // aliases even on Linux instead of letting platform choice activate behavior.
                    if (current == root)
                    {
                        foreach (var conventional in new[]
                        {
                            "skills", "hooks", "commands", "agents", "lsp", ".mcp.json", ".lsp.json",
                            ".claude-plugin", ".botnexus-plugin",
                        })
                        {
                            if (name.Equals(conventional, StringComparison.OrdinalIgnoreCase)
                                && !name.Equals(conventional, StringComparison.Ordinal))
                                errors.Add(new(field, $"Claude import requires canonical spelling '{conventional}'."));
                        }
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new("directory", $"Could not safely inspect Claude import tree: {ex.Message}"));
        }
        return errors;
    }

    private static bool IsPortableName(string name)
    {
        if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            return false;
        var stem = name.Split('.')[0];
        return !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
            "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsConventionalSkills(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is "./skills" or "skills",
        JsonValueKind.Array => value.GetArrayLength() == 1 && IsConventionalSkillsPath(value[0]),
        _ => false,
    };

    private static bool IsConventionalSkillsPath(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is "./skills" or "skills";

    private static void CheckDuplicates(JsonElement value, string path, List<PluginValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var childPath = path + "/" + property.Name;
                if (!names.Add(property.Name))
                    errors.Add(new(childPath, $"Duplicate Claude plugin field '{childPath}'."));
                CheckDuplicates(property.Value, childPath, errors);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                CheckDuplicates(item, path + "/" + index++, errors);
        }
    }
}

internal sealed record ImportedPlugin(PluginManifest Manifest, PluginManifestFormat Format);
