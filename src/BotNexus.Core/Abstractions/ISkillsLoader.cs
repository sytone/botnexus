using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for loading skill definitions for agents.</summary>
public interface ISkillsLoader
{
    /// <summary>Loads skill tool definitions for a given agent.</summary>
    Task<IReadOnlyList<ToolDefinition>> LoadSkillsAsync(string agentName, CancellationToken cancellationToken = default);
}
