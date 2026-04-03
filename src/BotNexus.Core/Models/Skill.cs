namespace BotNexus.Core.Models;

/// <summary>
/// Represents a skill loaded from a SKILL.md file.
/// Skills provide knowledge and instructions to agents.
/// </summary>
public sealed record Skill(
    string Name,
    string Description,
    string Content,
    string SourcePath,
    SkillScope Scope,
    string? Version = null,
    bool AlwaysLoad = false);

/// <summary>Scope of skill availability.</summary>
public enum SkillScope
{
    /// <summary>Skill is available globally to all agents.</summary>
    Global,
    
    /// <summary>Skill is available only to a specific agent.</summary>
    Agent
}
