using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Hooks;

// ── Before prompt build ──────────────────────────────────────────────

/// <summary>
/// Event raised before the system prompt is assembled for an agent invocation.
/// Gateway hook handlers receive this to inject context into the prompt.
/// </summary>
/// <param name="AgentId">The agent being invoked.</param>
/// <param name="CurrentPrompt">The current system prompt text before modifications.</param>
/// <param name="Messages">The conversation history being sent to the LLM.</param>
public sealed record BeforePromptBuildEvent(
    AgentId AgentId,
    string CurrentPrompt,
    IReadOnlyList<object> Messages);

/// <summary>
/// Result returned by a gateway hook handler after inspecting <see cref="BeforePromptBuildEvent"/>.
/// Allows prepending or appending context to the system prompt without replacing it.
/// </summary>
public sealed record BeforePromptBuildResult
{
    /// <summary>Text to prepend before the existing system prompt.</summary>
    public string? PrependSystemContext { get; init; }

    /// <summary>Text to append after the existing system prompt.</summary>
    public string? AppendSystemContext { get; init; }
}

// ── Before tool call ─────────────────────────────────────────────────

/// <summary>
/// Event raised before a tool call is executed by the agent runtime.
/// Gateway hook handlers receive this to enforce policies, modify arguments, or deny execution.
/// </summary>
/// <param name="AgentId">The agent making the tool call.</param>
/// <param name="ToolName">Name of the tool being called.</param>
/// <param name="ToolCallId">Unique identifier for this tool invocation.</param>
/// <param name="Arguments">The arguments the LLM provided for the tool call.</param>
public sealed record BeforeToolCallEvent(
    AgentId AgentId,
    string ToolName,
    string ToolCallId,
    IReadOnlyDictionary<string, object?> Arguments);

/// <summary>
/// Result returned by a gateway hook handler after inspecting <see cref="BeforeToolCallEvent"/>.
/// Can deny execution or modify the tool arguments before the tool runs.
/// </summary>
/// <remarks>
/// This is the <b>gateway-level</b> hook result used by extensions and hook handlers.
/// It is translated to the agent-level <c>Agent.Core.Hooks.BeforeToolCallResult</c>
/// by <c>InProcessIsolationStrategy</c> at the boundary. The gateway version adds
/// argument modification capability that the agent-level type does not have.
/// </remarks>
public sealed record BeforeToolCallResult
{
    /// <summary>When <c>true</c>, the tool call is blocked and an error result is returned to the LLM.</summary>
    public bool Denied { get; init; }

    /// <summary>Human-readable reason for denial. Sent to the LLM as the tool error message when <see cref="Denied"/> is <c>true</c>.</summary>
    public string? DenyReason { get; init; }

    /// <summary>
    /// Replacement arguments for the tool call. When non-null, these replace the original
    /// LLM-provided arguments before the tool executes. Use for argument sanitization or enrichment.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ModifiedArguments { get; init; }
}

// ── After tool call ──────────────────────────────────────────────────

/// <summary>
/// Event raised after a tool call completes. Gateway hook handlers receive this
/// for logging, auditing, or post-processing tool results.
/// </summary>
/// <param name="AgentId">The agent that made the tool call.</param>
/// <param name="ToolName">Name of the tool that was called.</param>
/// <param name="ToolCallId">Unique identifier for this tool invocation.</param>
/// <param name="Result">The tool's text result (may be null for tools that return structured data).</param>
/// <param name="IsError">Whether the tool execution reported an error.</param>
public sealed record AfterToolCallEvent(
    AgentId AgentId,
    string ToolName,
    string ToolCallId,
    string? Result,
    bool IsError);

/// <summary>
/// Result returned by a gateway hook handler after inspecting <see cref="AfterToolCallEvent"/>.
/// Currently a marker type — the gateway hook system does not support post-execution
/// result transformation. Use <c>Agent.Core.Hooks.AfterToolCallResult</c> at the
/// agent level for result overrides (content replacement, error flag changes).
/// </summary>
public sealed record AfterToolCallResult;
