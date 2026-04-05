using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Manages the lifecycle of running agent instances — creating, tracking, and stopping them.
/// Each instance is bound to an <see cref="AgentDescriptor"/> and a session.
/// </summary>
/// <remarks>
/// <para>
/// The supervisor sits between the Gateway routing layer and the isolation strategies.
/// When a message needs an agent, the supervisor either returns an existing instance
/// or creates one via the appropriate <see cref="IIsolationStrategy"/>.
/// </para>
/// <para>Implementations must be thread-safe.</para>
/// </remarks>
public interface IAgentSupervisor
{
    /// <summary>
    /// Gets or creates an agent instance for the given agent and session.
    /// If an instance already exists and is healthy, it is returned.
    /// Otherwise, a new one is created via the agent's configured isolation strategy.
    /// </summary>
    /// <param name="agentId">The registered agent ID.</param>
    /// <param name="sessionId">The session to bind the instance to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handle to the running agent instance.</returns>
    /// <exception cref="KeyNotFoundException">The agent ID is not registered.</exception>
    Task<IAgentHandle> GetOrCreateAsync(string agentId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a specific agent instance and releases its resources.
    /// </summary>
    /// <param name="agentId">The agent ID.</param>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StopAsync(string agentId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current instance metadata, or <c>null</c> if no instance exists.
    /// </summary>
    AgentInstance? GetInstance(string agentId, string sessionId);

    /// <summary>
    /// Gets all active agent instances.
    /// </summary>
    IReadOnlyList<AgentInstance> GetAllInstances();

    /// <summary>
    /// Stops all running agent instances. Called during Gateway shutdown.
    /// </summary>
    Task StopAllAsync(CancellationToken cancellationToken = default);
}
