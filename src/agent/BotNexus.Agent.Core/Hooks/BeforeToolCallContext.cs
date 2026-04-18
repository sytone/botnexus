using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Hooks;

/// <summary>
/// Provides context for pre-tool-call interception.
/// </summary>
/// <param name="AssistantMessage">The assistant message requesting the tool call.</param>
/// <param name="ToolCallRequest">The requested tool call payload (id, name, arguments).</param>
/// <param name="ValidatedArgs">The validated tool arguments (after PrepareArgumentsAsync).</param>
/// <param name="AgentContext">The current agent context (system prompt, messages, tools).</param>
/// <remarks>
/// Passed to BeforeToolCallDelegate before tool execution.
/// Use to inspect or block tool calls based on policy, rate limits, or validation.
/// </remarks>
public record BeforeToolCallContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallContent ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentContext AgentContext);
