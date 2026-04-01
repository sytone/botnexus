using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;

namespace BotNexus.Agent.Tools;

/// <summary>Central registry of executable tools for an agent.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a tool, replacing any existing tool with the same name.</summary>
    public void Register(ITool tool) => _tools[tool.Definition.Name] = tool;

    /// <summary>Registers multiple tools at once.</summary>
    public void RegisterRange(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools) Register(tool);
    }

    /// <summary>Gets a tool by name, or <c>null</c> if not found.</summary>
    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    /// <summary>Returns <c>true</c> when a tool with the given name is registered.</summary>
    public bool Contains(string name) => _tools.ContainsKey(name);

    /// <summary>Returns all registered tool definitions (for the LLM function-calling payload).</summary>
    public IReadOnlyList<ToolDefinition> GetDefinitions()
        => [.. _tools.Values.Select(t => t.Definition)];

    /// <summary>Returns all registered tool names.</summary>
    public IReadOnlyList<string> GetNames()
        => [.. _tools.Keys];

    /// <summary>Removes a tool by name. Returns <c>true</c> if it was present.</summary>
    public bool Remove(string name) => _tools.Remove(name);

    /// <summary>Executes a tool call and returns the result string.</summary>
    public async Task<string> ExecuteAsync(ToolCallRequest toolCall, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(toolCall.ToolName, out var tool))
            return $"Error: Tool '{toolCall.ToolName}' not found.";

        try
        {
            return await tool.ExecuteAsync(toolCall.Arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Error executing tool '{toolCall.ToolName}': {ex.Message}";
        }
    }
}
