using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Enables agents to call sub-agents (same Gateway) and cross-agents (remote Gateways).
/// This is the coordination surface for multi-agent workflows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sub-agent calls</b> are local: the parent agent's tool invokes a child agent
/// on the same Gateway. The supervisor manages the child's lifecycle, and the parent
/// receives the response as a tool result.
/// </para>
/// <para>
/// <b>Cross-agent calls</b> are local-first: calls without a target endpoint are routed
/// through the local supervisor into a cross-agent scoped session. Remote endpoint support
/// is reserved for a future implementation.
/// </para>
/// </remarks>
public interface IAgentCommunicator
{
    /// <summary>
    /// Calls a sub-agent on the same Gateway and returns its response.
    /// The sub-agent runs in a new session scoped to the parent.
    /// </summary>
    /// <param name="parentAgentId">The calling agent's ID (for audit and scoping).</param>
    /// <param name="parentSessionId">The calling agent's session ID.</param>
    /// <param name="childAgentId">The target sub-agent's registered ID.</param>
    /// <param name="message">The message to send to the sub-agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sub-agent's response.</returns>
    /// <exception cref="KeyNotFoundException">The child agent ID is not registered.</exception>
    Task<AgentResponse> CallSubAgentAsync(
        string parentAgentId,
        string parentSessionId,
        string childAgentId,
        string message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls another agent and returns its response.
    /// If <paramref name="targetEndpoint"/> is empty, the call is handled locally.
    /// Non-empty endpoints are currently unsupported.
    /// </summary>
    /// <param name="sourceAgentId">The calling agent's ID.</param>
    /// <param name="targetEndpoint">The remote Gateway's base URL.</param>
    /// <param name="targetAgentId">The remote agent's ID.</param>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The remote agent's response.</returns>
    /// <exception cref="NotSupportedException">Remote endpoint calls are not supported yet.</exception>
    /// <exception cref="KeyNotFoundException">The target agent ID is not registered.</exception>
    Task<AgentResponse> CallCrossAgentAsync(
        string sourceAgentId,
        string targetEndpoint,
        string targetAgentId,
        string message,
        CancellationToken cancellationToken = default);
}
