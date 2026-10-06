using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>
/// Provides context for tool result transformation.
/// </summary>
/// <param name="AssistantMessage">The assistant message requesting the tool call.</param>
/// <param name="ToolCallRequest">The requested tool call payload (id, name, arguments).</param>
/// <param name="ValidatedArgs">The validated tool arguments (after PrepareArgumentsAsync).</param>
/// <param name="Result">The tool execution result (before transformation).</param>
/// <param name="IsError">Indicates whether execution failed (exception or validation error).</param>
/// <param name="AgentContext">The current agent context (system prompt, messages, tools).</param>
/// <remarks>
/// Passed to ToolResultTransformer after tool execution.
/// Use to transform, filter, redact, or override tool results before they reach the LLM.
/// </remarks>
public record ToolResultTransformContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallContent ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentToolResult Result,
    bool IsError,
    AgentContext AgentContext);