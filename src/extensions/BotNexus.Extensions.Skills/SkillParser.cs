using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Parses a SKILL.md file into a <see cref="SkillDefinition"/>.
/// Spec: https://agentskills.io/specification
/// </summary>
public static class SkillParser
{
    private static readonly Regex NameValidation = new(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.Compiled);

    public static SkillDefinition Parse(string directoryName, string markdown, string sourcePath, SkillSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        ArgumentNullException.ThrowIfNull(markdown);

        // Strip UTF-8 BOM if present — it breaks frontmatter detection
        markdown = markdown.TrimStart('\uFEFF');

        var (frontmatter, content) = SplitFrontmatter(markdown);

        string? name = null;
        string? description = null;
        string? license = null;
        string? compatibility = null;
        string? allowedTools = null;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (frontmatter is not null)
        {
            var fields = ParseFrontmatter(frontmatter);
            name = fields.GetValueOrDefault("name");
            description = fields.GetValueOrDefault("description");
            license = fields.GetValueOrDefault("license");
            compatibility = fields.GetValueOrDefault("compatibility");
            allowedTools = fields.GetValueOrDefault("allowed-tools");
            metadata = ExtractMetadata(frontmatter);
        }

        var disableModelInvocation = false;
        if (frontmatter is not null)
        {
            var rawDisable = ParseFrontmatter(frontmatter).GetValueOrDefault("disable-model-invocation");
            disableModelInvocation = rawDisable is not null &&
                bool.TryParse(rawDisable, out var parsed) && parsed;
        }

        return new SkillDefinition
        {
            Name = name ?? directoryName,
            Description = description ?? string.Empty,
            License = license,
            Compatibility = compatibility,
            AllowedTools = allowedTools,
            DisableModelInvocation = disableModelInvocation,
            Metadata = metadata,
            Content = content.Trim(),
            SourcePath = sourcePath,
            Source = source
        };
    }

    /// <summary>Validates a skill name per spec: lowercase alphanumeric + hyphens, max 64 chars, no leading/trailing/consecutive hyphens.</summary>
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            return false;
        if (name.Contains("--"))
            return false;
        return NameValidation.IsMatch(name);
    }

    private static (string? frontmatter, string content) SplitFrontmatter(string markdown)
    {
        var lines = markdown.Split(["\r\n", "\n"], StringSplitOptions.None);
        var firstNonEmpty = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (firstNonEmpty < 0 || lines[firstNonEmpty].Trim() != "---")
            return (null, markdown.Trim());

        var closingIndex = Array.FindIndex(lines, firstNonEmpty + 1, l => l.Trim() == "---");
        if (closingIndex < 0)
            return (null, markdown.Trim());

        var frontmatter = string.Join("\n", lines[(firstNonEmpty + 1)..closingIndex]);
        var content = string.Join("\n", lines[(closingIndex + 1)..]);
        return (frontmatter, content.Trim());
    }

    private static Dictionary<string, string> ParseFrontmatter(string frontmatter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = frontmatter.Split('\n');

        string? pendingBlockKey = null;     // key waiting for block scalar content
        bool pendingBlockIsFolded = false;  // true = '>' (fold newlines), false = '|' (preserve)
        var pendingBlockLines = new List<string>();

        void FlushPendingBlock()
        {
            if (pendingBlockKey is null) return;
            var value = pendingBlockIsFolded
                ? string.Join(" ", pendingBlockLines).TrimEnd()
                : string.Join("\n", pendingBlockLines).TrimEnd();
            if (!string.IsNullOrWhiteSpace(value))
                result[pendingBlockKey] = value;
            pendingBlockKey = null;
            pendingBlockLines.Clear();
        }

        foreach (var line in lines)
        {
            // Collecting indented block scalar content
            if (pendingBlockKey is not null)
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    // Strip one level of leading whitespace (common indentation)
                    pendingBlockLines.Add(line.TrimStart());
                    continue;
                }

                // First non-indented line ends the block
                FlushPendingBlock();
            }

            // Skip remaining indented lines that aren't block scalar content
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;

            var sep = line.IndexOf(':');
            if (sep <= 0)
                continue;

            var key = line[..sep].Trim();
            var rawValue = line[(sep + 1)..].Trim().Trim('"', '\'');

            if (string.IsNullOrWhiteSpace(key))
                continue;

            // Detect block scalar indicators
            if (rawValue == "|")
            {
                pendingBlockKey = key;
                pendingBlockIsFolded = false;
                pendingBlockLines.Clear();
                continue;
            }

            if (rawValue == ">")
            {
                pendingBlockKey = key;
                pendingBlockIsFolded = true;
                pendingBlockLines.Clear();
                continue;
            }

            // Skip block-level keys (e.g. metadata:) with no inline value
            if (!string.IsNullOrWhiteSpace(rawValue))
                result[key] = rawValue;
        }

        // Flush any block scalar that ended at EOF
        FlushPendingBlock();

        return result;
    }

    private static Dictionary<string, string> ExtractMetadata(string frontmatter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = frontmatter.Split('\n');
        var inMetadata = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("metadata:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("metadata:", StringComparison.OrdinalIgnoreCase))
            {
                // Check if it's a block start (no value after colon)
                var afterColon = trimmed["metadata:".Length..].Trim();
                if (string.IsNullOrEmpty(afterColon))
                {
                    inMetadata = true;
                    continue;
                }
            }

            if (inMetadata)
            {
                if (line.Length > 0 && line[0] != ' ' && line[0] != '\t')
                {
                    inMetadata = false;
                    continue;
                }

                var sep = trimmed.IndexOf(':');
                if (sep > 0)
                {
                    var key = trimmed[..sep].Trim();
                    var value = trimmed[(sep + 1)..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(key))
                        result[key] = value;
                }
            }
        }

        return result;
    }
}
