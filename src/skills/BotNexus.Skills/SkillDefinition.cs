namespace BotNexus.Skills;

/// <summary>
/// A discovered skill definition parsed from a SKILL.md file.
/// Follows the Agent Skills specification: https://agentskills.io/specification
/// </summary>
public sealed record SkillDefinition
{
    /// <summary>Skill name (required, must match directory name). Lowercase, hyphens, no consecutive hyphens.</summary>
    public required string Name { get; init; }

    /// <summary>What the skill does and when to use it (required, max 1024 chars).</summary>
    public required string Description { get; init; }

    /// <summary>License name or reference to bundled license file.</summary>
    public string? License { get; init; }

    /// <summary>Environment requirements (intended product, system packages, network access).</summary>
    public string? Compatibility { get; init; }

    /// <summary>Arbitrary key-value metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Space-delimited list of pre-approved tools. Experimental.</summary>
    public string? AllowedTools { get; init; }

    /// <summary>The markdown body (instructions) after frontmatter.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Filesystem path to the skill directory.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Where this skill was discovered from.</summary>
    public required SkillSource Source { get; init; }
}

/// <summary>Where a skill was discovered from (lower values overridden by higher).</summary>
public enum SkillSource
{
    Global = 0,
    Agent = 1,
    Workspace = 2
}
