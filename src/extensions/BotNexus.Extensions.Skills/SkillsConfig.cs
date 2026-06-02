namespace BotNexus.Extensions.Skills;

/// <summary>
/// Per-agent skills configuration. Read from agent's ExtensionConfig["botnexus-skills"].
/// </summary>
public sealed class SkillsConfig
{
    /// <summary>Whether the skills system is enabled for this agent. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Skill names to always load for this agent.</summary>
    public List<string>? AutoLoad { get; set; }

    /// <summary>Skill names explicitly denied — these are never loaded.</summary>
    public List<string>? Disabled { get; set; }

    /// <summary>Skill names explicitly allowed. Null means all skills are allowed.</summary>
    public List<string>? Allowed { get; set; }

    /// <summary>Maximum number of skills to load simultaneously.</summary>
    public int MaxLoadedSkills { get; set; } = 20;

    /// <summary>Maximum total characters of skill content in the prompt.</summary>
    public int MaxSkillContentChars { get; set; } = 100_000;

    // ── SkillManagerTool gates ──────────────────────────────────────────────

    /// <summary>
    /// Allow the agent to create and edit skills via the skill_manage tool.
    /// When false (default), the tool is not contributed and write operations are blocked.
    /// </summary>
    public bool AllowSkillCreation { get; set; } = false;

    /// <summary>
    /// Allow the agent to delete skills and remove supporting files via the skill_manage tool.
    /// When false (default), delete and remove_file actions are blocked.
    /// Requires <see cref="AllowSkillCreation"/> to also be true.
    /// </summary>
    public bool AllowSkillDeletion { get; set; } = false;
}
