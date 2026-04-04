using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Base;

namespace BotNexus.AgentCore.Configuration;

/// <summary>
/// Defines optional initial state values used to seed a new <see cref="AgentState"/>.
/// </summary>
/// <param name="SystemPrompt">The initial system prompt.</param>
/// <param name="Model">The initial model definition.</param>
/// <param name="ThinkingLevel">The initial thinking level.</param>
/// <param name="Tools">The initial tool list.</param>
/// <param name="Messages">The initial message timeline.</param>
public record AgentInitialState(
    string? SystemPrompt = null,
    ModelDefinition? Model = null,
    ThinkingLevel? ThinkingLevel = null,
    IReadOnlyList<IAgentTool>? Tools = null,
    IReadOnlyList<AgentMessage>? Messages = null);
