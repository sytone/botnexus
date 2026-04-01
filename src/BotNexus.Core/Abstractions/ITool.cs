using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for an executable tool that can be called by the LLM.</summary>
public interface ITool
{
    /// <summary>The tool's definition (name, description, parameters).</summary>
    ToolDefinition Definition { get; }

    /// <summary>Executes the tool with the given arguments.</summary>
    Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default);
}
