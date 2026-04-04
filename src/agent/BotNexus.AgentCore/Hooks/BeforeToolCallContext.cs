using BotNexus.AgentCore.Types;
using BotNexus.Core.Models;

namespace BotNexus.AgentCore.Hooks;

/// <summary>
/// Provides context for pre-tool-call interception.
/// </summary>
/// <param name="AssistantMessage">The assistant message requesting the tool call.</param>
/// <param name="ToolCallRequest">The requested tool call payload.</param>
/// <param name="ValidatedArgs">The validated tool arguments.</param>
/// <param name="AgentContext">The current agent context.</param>
public record BeforeToolCallContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallRequest ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentContext AgentContext);
