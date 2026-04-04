using BotNexus.AgentCore.Types;
using BotNexus.Core.Models;

namespace BotNexus.AgentCore.Tools;

/// <summary>
/// Defines a pi-mono compatible agent tool contract.
/// </summary>
public interface IAgentTool
{
    /// <summary>
    /// Gets the unique tool name exposed to the model.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a human-readable label for diagnostics and UX.
    /// </summary>
    string Label { get; }

    /// <summary>
    /// Gets the tool schema definition exposed to the model.
    /// </summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// Validates and prepares tool call arguments before execution.
    /// </summary>
    /// <param name="arguments">The raw tool call arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A validated argument dictionary.</returns>
    Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the tool with validated arguments.
    /// </summary>
    /// <param name="arguments">The validated tool arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The normalized tool result.</returns>
    Task<AgentToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);
}
