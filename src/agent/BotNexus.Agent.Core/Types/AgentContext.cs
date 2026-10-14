using BotNexus.Agent.Core.Tools;

namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Captures the context passed through the agent loop for transformation and hook evaluation.
/// </summary>
/// <param name="SystemPrompt">The active system prompt (sent to the LLM at context start).</param>
/// <param name="Messages">The current message timeline (full conversation history).</param>
/// <param name="Tools">The currently available tools (registered in AgentState.Tools).</param>
/// <remarks>
/// AgentContext is a snapshot of the current agent state at a specific point in the loop.
/// Its message list is passed to AgentContextTransformer and ProviderMessageTransformer;
/// the snapshot is also available to tool execution policy and result transformer delegates.
/// </remarks>
public record AgentContext(
    string? SystemPrompt,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<IAgentTool> Tools);
