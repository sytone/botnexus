using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Configuration;

/// <summary>
/// Defines optional initial state values used to seed a new <see cref="AgentState"/>.
/// </summary>
/// <param name="SystemPrompt">The initial system prompt (sent to the LLM at the start of each context).</param>
/// <param name="Model">The initial model definition (overrides AgentOptions.Model if set).</param>
/// <param name="ThinkingLevel">The initial thinking level for extended reasoning models.</param>
/// <param name="Tools">The initial tool list (available for model invocation and execution).</param>
/// <param name="Messages">The initial message timeline (prefills conversation history).</param>
/// <remarks>
/// All fields are optional. If not provided, the agent starts with empty state (except Model, which is required).
/// Use Messages to prefill conversation history or inject system messages.
/// </remarks>
public record AgentInitialState(
    string? SystemPrompt = null,
    LlmModel? Model = null,
    ThinkingLevel? ThinkingLevel = null,
    IReadOnlyList<IAgentTool>? Tools = null,
    IReadOnlyList<AgentMessage>? Messages = null);
