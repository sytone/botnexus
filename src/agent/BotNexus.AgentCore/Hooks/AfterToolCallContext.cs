using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Hooks;

/// <summary>
/// Provides context for post-tool-call interception.
/// </summary>
/// <param name="AssistantMessage">The assistant message requesting the tool call.</param>
/// <param name="ToolCallRequest">The requested tool call payload.</param>
/// <param name="ValidatedArgs">The validated tool arguments.</param>
/// <param name="Result">The tool execution result.</param>
/// <param name="IsError">Indicates whether execution failed.</param>
/// <param name="AgentContext">The current agent context.</param>
public record AfterToolCallContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallContent ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentToolResult Result,
    bool IsError,
    AgentContext AgentContext);
