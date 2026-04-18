using BotNexus.Agent.Core.Tools;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Registry for discovering and resolving agent tools.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Gets all registered tools.
    /// </summary>
    IReadOnlyList<IAgentTool> GetAll();

    /// <summary>
    /// Gets a tool by name.
    /// </summary>
    /// <param name="name">Tool name (case-insensitive).</param>
    /// <returns>The tool, or null if not found.</returns>
    IAgentTool? GetByName(string name);

    /// <summary>
    /// Resolves a list of tools by their identifiers.
    /// </summary>
    /// <param name="toolIds">Tool names to resolve.</param>
    /// <returns>List of resolved tools (missing tools are silently skipped).</returns>
    IReadOnlyList<IAgentTool> ResolveTools(IEnumerable<string> toolIds);
}
