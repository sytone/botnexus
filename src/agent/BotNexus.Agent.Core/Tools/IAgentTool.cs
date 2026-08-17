using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tools;

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
    /// Case-insensitive comparison (StringComparison.OrdinalIgnoreCase) is used during lookup.
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
    /// Optional per-tool execution timeout hint. When set, <c>ToolExecutor</c> uses
    /// <c>max(configuredSafetyCap, DefaultTimeout)</c> so long-running tools are not
    /// prematurely cancelled by the global safety cap. Return <c>null</c> to defer
    /// entirely to the configured safety cap.
    /// </summary>
    TimeSpan? DefaultTimeout => null;

    /// <summary>
    /// Optional declaration of the invocation argument that carries a caller-requested timeout,
    /// together with the unit that argument is expressed in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ToolExecutor</c> consults this - and only this - when deciding whether to widen its
    /// per-tool cancellation budget for an explicitly requested longer run. Returning <c>null</c>
    /// (the default) means the tool opts out: the executor inspects no arguments at all.
    /// </para>
    /// <para>
    /// The executor must never infer a unit from an argument's name. <c>timeout</c> means seconds
    /// in <c>ShellTool</c> and milliseconds in <c>ProcessTool</c>, so name-based inference inflated
    /// millisecond budgets by 1000x and silently disabled the safety cap (issue #2955).
    /// </para>
    /// </remarks>
    ToolTimeoutArgument? TimeoutArgument => null;

    /// <summary>
    /// Declares where the content this tool returns originates, from the closed
    /// <see cref="ToolContentSource"/> vocabulary. Consumed by <c>ToolExecutor</c> to accumulate
    /// turn-level taint, which in turn quarantines memory writes made on a turn that read foreign
    /// content (issue #2519).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Defaults to <see cref="ToolContentSource.Unknown"/>, which taints.</b> This is the
    /// fail-closed posture required by the issue: a tool that has not been classified - including
    /// one contributed by an extension written after this shipped - must not silently count as
    /// trusted. The cost of the default is over-quarantining until a tool is classified, which is
    /// recoverable; the cost of defaulting to <see cref="ToolContentSource.Local"/> would be a
    /// silent laundering path, which is not.
    /// </para>
    /// <para>
    /// Classify by the <i>origin of the returned bytes</i>, never by the tool's power or blast
    /// radius. <c>shell</c> can do far more damage than <c>web_fetch</c> and is nonetheless
    /// <see cref="ToolContentSource.Local"/>, because its output is produced inside the trust
    /// domain the agent already occupies.
    /// </para>
    /// </remarks>
    string ContentSource => ToolContentSource.Unknown;

    /// <summary>
    /// Optional one-line snippet for system prompt tool listing.
    /// </summary>
    string? GetPromptSnippet() => null;

    /// <summary>
    /// Optional additional guidelines contributed by this tool.
    /// </summary>
    IReadOnlyList<string> GetPromptGuidelines() => [];
}
