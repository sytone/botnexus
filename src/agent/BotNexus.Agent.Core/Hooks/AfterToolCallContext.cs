using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Hooks;

/// <summary>
/// Provides context for post-tool-call interception.
/// </summary>
/// <param name="AssistantMessage">The assistant message requesting the tool call.</param>
/// <param name="ToolCallRequest">The requested tool call payload (id, name, arguments).</param>
/// <param name="ValidatedArgs">The validated tool arguments (after PrepareArgumentsAsync).</param>
/// <param name="Result">The tool execution result (before hook transformation).</param>
/// <param name="IsError">Indicates whether execution failed (exception or validation error).</param>
/// <param name="AgentContext">The current agent context (system prompt, messages, tools).</param>
/// <remarks>
/// Passed to AfterToolCallDelegate after tool execution.
/// Use to transform, filter, redact, or override tool results before they reach the LLM.
/// </remarks>
public record AfterToolCallContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallContent ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentToolResult Result,
    bool IsError,
    AgentContext AgentContext);
