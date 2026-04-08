namespace BotNexus.Extensions.Skills;

/// <summary>
/// Per-agent skills configuration. Controls which skills are loaded and access filtering.
/// </summary>
public sealed class SkillsConfig
{
    /// <summary>Whether the skills system is enabled for this agent. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Skill names to always load for this agent (in addition to agent-specific skills).</summary>
    public List<string>? AutoLoad { get; set; }

    /// <summary>Skill names explicitly denied — these are never loaded regardless of other settings.</summary>
    public List<string>? Disabled { get; set; }

    /// <summary>Skill names explicitly allowed. Null means all discovered skills are allowed. When set, only these skills can load.</summary>
    public List<string>? Allowed { get; set; }

    /// <summary>Maximum number of skills to load simultaneously into the prompt.</summary>
    public int MaxLoadedSkills { get; set; } = 20;

    /// <summary>Maximum total characters of skill content in the prompt.</summary>
    public int MaxSkillContentChars { get; set; } = 100_000;
}
