using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Tools;

/// <summary>
/// Defines a pi-mono compatible agent tool contract.
/// </summary>
/// <remarks>
/// Tools are registered in AgentState.Tools and exposed to the model during generation.
/// The agent loop calls PrepareArgumentsAsync for validation, then ExecuteAsync for execution.
/// Tools must be thread-safe if ToolExecutionMode.Parallel is used.
/// </remarks>
public interface IAgentTool
{
    /// <summary>
    /// Gets the unique tool name exposed to the model.
    /// </summary>
    /// <remarks>
    /// Must match the Name in Definition. Used for routing tool calls.
    /// Case-insensitive comparison is used during lookup.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// Gets a human-readable label for diagnostics and UX.
    /// </summary>
    /// <remarks>
    /// Displayed in logs, error messages, and event payloads.
    /// </remarks>
    string Label { get; }

    /// <summary>
    /// Gets the tool schema definition exposed to the model.
    /// </summary>
    /// <remarks>
    /// Defines the tool's name, description, and JSON Schema parameters.
    /// The model uses this to decide when and how to call the tool.
    /// </remarks>
    Tool Definition { get; }

    /// <summary>
    /// Validates and prepares tool call arguments before execution.
    /// </summary>
    /// <param name="arguments">The raw tool call arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A validated argument dictionary.</returns>
    /// <remarks>
    /// <para>
    /// Called before ExecuteAsync to validate, coerce, or enrich arguments.
    /// Throw exceptions for validation failures — they are caught and converted to error tool results.
    /// </para>
    /// <para>
    /// For parallel execution mode, this method is called sequentially before parallel execution begins.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the tool with validated arguments.
    /// </summary>
    /// <param name="toolCallId">The unique tool call identifier for this execution.</param>
    /// <param name="arguments">The validated tool arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="onUpdate">Optional callback for partial execution updates.</param>
    /// <returns>The normalized tool result.</returns>
    /// <remarks>
    /// <para>
    /// Called after PrepareArgumentsAsync succeeds. Return an AgentToolResult with text or image content.
    /// Throw exceptions for execution failures — they are caught and converted to error tool results.
    /// </para>
    /// <para>
    /// For parallel execution mode, multiple tools may execute concurrently. Ensure thread safety.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public async Task&lt;AgentToolResult&gt; ExecuteAsync(
    ///     string toolCallId,
    ///     IReadOnlyDictionary&lt;string, object?&gt; arguments,
    ///     CancellationToken cancellationToken,
    ///     AgentToolUpdateCallback? onUpdate = null)
    /// {
    ///     var query = arguments["query"]?.ToString();
    ///     var result = await SearchAsync(query, cancellationToken);
    ///     return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, result)]);
    /// }
    /// </code>
    /// </example>
    Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null);

    /// <summary>
    /// Optional one-line snippet for system prompt tool listing.
    /// </summary>
    string? GetPromptSnippet() => null;

    /// <summary>
    /// Optional additional guidelines contributed by this tool.
    /// </summary>
    IReadOnlyList<string> GetPromptGuidelines() => [];
}
