using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Default in-memory tool registry that resolves tools from DI.
/// </summary>
public sealed class DefaultToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    /// <summary>
    /// Initializes the tool registry with a collection of tools.
    /// </summary>
    /// <param name="tools">All available tools to register.</param>
    public DefaultToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> GetAll() => [.. _tools.Values];

    /// <inheritdoc />
    public IAgentTool? GetByName(string name) =>
        _tools.GetValueOrDefault(name);

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> ResolveTools(IEnumerable<string> toolIds)
    {
        var resolved = new List<IAgentTool>();
        foreach (var id in toolIds)
        {
            if (_tools.TryGetValue(id, out var tool))
                resolved.Add(tool);
        }
        return resolved;
    }
}
