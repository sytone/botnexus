using BotNexus.AgentCore.Tools;

namespace BotNexus.AgentCore.Types;

/// <summary>
/// Captures the context passed through the agent loop for transformation and hook evaluation.
/// </summary>
/// <param name="SystemPrompt">The active system prompt.</param>
/// <param name="Messages">The current message timeline.</param>
/// <param name="Tools">The currently available tools.</param>
public record AgentContext(
    string? SystemPrompt,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<IAgentTool> Tools);
