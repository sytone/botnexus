using BotNexus.Extensions.Skills.Security;

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

    /// <summary>
    /// Trust verification mode for skill scripts.
    /// Disabled = no verification (default), Warn = log warning, Enforce = block untrusted.
    /// </summary>
    public SkillTrustMode TrustMode { get; set; } = SkillTrustMode.Disabled;

    // ── SkillManagerTool gates ──────────────────────────────────────────────

    /// <summary>
    /// Allow the agent to create and edit skills via the skill_manage tool.
    /// Defaults to true -- the Skills extension is opt-in, so creation/editing should be
    /// available by default when the extension is enabled.
    /// Set to false to explicitly restrict write access to skills.
    /// </summary>
    public bool AllowSkillCreation { get; set; } = true;

    /// <summary>
    /// Allow the agent to delete skills and remove supporting files via the skill_manage tool.
    /// Defaults to true -- see <see cref="AllowSkillCreation"/> rationale.
    /// Requires <see cref="AllowSkillCreation"/> to also be true.
    /// Set to false to explicitly prevent skill deletion.
    /// </summary>
    public bool AllowSkillDeletion { get; set; } = true;

    /// <summary>
    /// Allow the agent to create, edit, and delete SHARED (all-agent) skills under the global
    /// skills directory via the skill_manage tool with scope "shared". Defaults to FALSE.
    /// Shared skills are visible to every agent, so a change here has a wide blast radius --
    /// this gate is opt-in. Deleting shared skills additionally requires <see cref="AllowSkillDeletion"/>.
    /// </summary>
    public bool AllowSharedSkillManagement { get; set; }
}
