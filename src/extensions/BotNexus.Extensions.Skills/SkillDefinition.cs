namespace BotNexus.Extensions.Skills;

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

    /// <summary>When true, this skill is excluded from model context. Used for agent-internal skills.</summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>The markdown body (instructions) after frontmatter.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Filesystem path to the skill directory.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Where this skill was discovered from.</summary>
    public required SkillSource Source { get; init; }

    /// <summary>
    /// Support files bundled with the skill (references/, templates/, scripts/, assets/),
    /// discovered so they can be surfaced as load-on-demand context without injecting the
    /// entire skill directory into the model prompt. Empty when the skill has no support files.
    /// </summary>
    public IReadOnlyList<SkillLinkedFile> LinkedFiles { get; init; } = [];
}

/// <summary>
/// A support file bundled inside a skill directory (under references/, templates/, scripts/,
/// or assets/). Exposed as first-class load-on-demand context: the agent can view an individual
/// file via the skills tool's <c>view_file</c> action rather than loading the whole directory.
/// </summary>
public sealed record SkillLinkedFile
{
    /// <summary>
    /// Path relative to the skill directory, using forward slashes
    /// (e.g. <c>references/api-reference.md</c>). Stable across platforms.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// The top-level support directory this file lives under
    /// (<c>references</c>, <c>templates</c>, <c>scripts</c>, or <c>assets</c>).
    /// Used to group the "Linked files" listing on load.
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>Size of the file in bytes, used for display in the linked-file listing.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>Where a skill was discovered from (lower values overridden by higher).</summary>
public enum SkillSource
{
    Global = 0,
    Agent = 1,
    Workspace = 2
}
