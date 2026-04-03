using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for loading skill definitions for agents.</summary>
public interface ISkillsLoader
{
    /// <summary>
    /// Loads skills for a given agent from global and per-agent skill directories.
    /// Applies DisabledSkills filtering with wildcard support.
    /// Agent-level skills override global skills with the same name.
    /// </summary>
    Task<IReadOnlyList<Skill>> LoadSkillsAsync(string agentName, CancellationToken cancellationToken = default);
}
