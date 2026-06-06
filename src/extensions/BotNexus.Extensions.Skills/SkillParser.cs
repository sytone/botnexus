using System.Text.RegularExpressions;
using BotNexus.Yaml;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Parses a SKILL.md file into a <see cref="SkillDefinition"/>.
/// Spec: https://agentskills.io/specification
/// </summary>
public static class SkillParser
{
    private static readonly Regex NameValidation = new(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.Compiled);

    /// <summary>
    /// The YAML frontmatter parser used for skill file parsing.
    /// Replace with a custom implementation for testing or extended YAML support.
    /// </summary>
    internal static IYamlFrontmatterParser YamlParser { get; set; } = SimpleYamlFrontmatterParser.Instance;

    public static SkillDefinition Parse(string directoryName, string markdown, string sourcePath, SkillSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        ArgumentNullException.ThrowIfNull(markdown);

        // Strip UTF-8 BOM if present - it breaks frontmatter detection
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
            var fields = YamlParser.Parse(frontmatter);
            name         = fields.GetValueOrDefault("name");
            description  = fields.GetValueOrDefault("description");
            license      = fields.GetValueOrDefault("license");
            compatibility = fields.GetValueOrDefault("compatibility");
            allowedTools = fields.GetValueOrDefault("allowed-tools");
            metadata     = new Dictionary<string, string>(
                               YamlParser.ParseNested(frontmatter, "metadata"),
                               StringComparer.OrdinalIgnoreCase);
        }

        var disableModelInvocation = false;
        if (frontmatter is not null)
        {
            var fields     = YamlParser.Parse(frontmatter);
            var rawDisable = fields.GetValueOrDefault("disable-model-invocation");
            disableModelInvocation = rawDisable is not null &&
                bool.TryParse(rawDisable, out var parsed) && parsed;
        }

        return new SkillDefinition
        {
            Name                   = name ?? directoryName,
            Description            = description ?? string.Empty,
            License                = license,
            Compatibility          = compatibility,
            AllowedTools           = allowedTools,
            DisableModelInvocation = disableModelInvocation,
            Metadata               = metadata,
            Content                = content.Trim(),
            SourcePath             = sourcePath,
            Source                 = source
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

    // ── Frontmatter splitting ─────────────────────────────────────────────────

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
        var content     = string.Join("\n", lines[(closingIndex + 1)..]);
        return (frontmatter, content.Trim());
    }
}
