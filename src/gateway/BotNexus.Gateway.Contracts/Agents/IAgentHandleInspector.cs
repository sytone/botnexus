using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Optional runtime contract for retrieving active agent handles by agent/session identifiers.
/// </summary>
public interface IAgentHandleInspector
{
    /// <summary>
    /// Gets an active agent handle for the given agent/session pair, or <c>null</c> when unavailable.
    /// </summary>
    IAgentHandle? GetHandle(AgentId agentId, SessionId sessionId);

    /// <summary>
    /// Resolves a tool by name from an active agent/session handle, or <c>null</c> when unavailable.
    /// </summary>
    IAgentTool? ResolveTool(AgentId agentId, SessionId sessionId, string toolName);

    /// <summary>
    /// Gets context diagnostics for an active agent/session handle, or <c>null</c> when unavailable.
    /// </summary>
    ContextDiagnostics? GetContextDiagnostics();
}
