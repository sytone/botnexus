namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Optional runtime contract for retrieving active agent handles by agent/session identifiers.
/// </summary>
public interface IAgentHandleInspector
{
    /// <summary>
    /// Gets an active agent handle for the given agent/session pair, or <c>null</c> when unavailable.
    /// </summary>
    IAgentHandle? GetHandle(string agentId, string sessionId);
}
