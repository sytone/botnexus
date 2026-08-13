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

    /// <summary>
    /// The context window in tokens that this handle's run is actually bound to, or
    /// <see langword="null"/> when it cannot be established (#3091).
    /// </summary>
    /// <remarks>
    /// Reported alongside <see cref="GetContextDiagnostics"/> so a consumer can compute real
    /// headroom. The default implementation returns <see langword="null"/> - "I do not know" - which
    /// is the honest answer for an inspector that has no model binding. It must never be replaced
    /// with a plausible-looking constant: a wrong window is undetectable by the caller, an absent
    /// one is not.
    /// </remarks>
    int? GetContextWindowTokens() => null;
}
