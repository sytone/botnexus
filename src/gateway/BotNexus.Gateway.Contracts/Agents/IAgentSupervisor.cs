using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

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
    Task<IAgentHandle> GetOrCreateAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a specific agent instance and releases its resources.
    /// </summary>
    /// <param name="agentId">The agent ID.</param>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StopAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserves the exact resident handle's idle run admission and prevents replacement
    /// creation until disposal. Null means stale, busy, or unsupported. Never creates an agent.
    /// </summary>
    IDisposable? TryReserveMaintenance(IAgentHandle expectedHandle) => null;

    /// <summary>
    /// Removes and disposes only the exact captured resident handle. False means no matching
    /// resident exists; a replacement must never be stopped. Unsupported implementations fail closed.
    /// Once removal commits, cleanup is not cancelled by the request token.
    /// </summary>
    Task<bool> StopIfCurrentAsync(IAgentHandle expectedHandle, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Conditional handle eviction is unsupported.");

    /// <summary>
    /// Gets the current instance metadata, or <c>null</c> if no instance exists.
    /// </summary>
    AgentInstance? GetInstance(AgentId agentId, SessionId sessionId);

    /// <summary>
    /// Gets the live agent handle for the given session, or <c>null</c> if no instance is running.
    /// </summary>
    IAgentHandle? GetHandle(AgentId agentId, SessionId sessionId);

    /// <summary>
    /// Gets all active agent instances.
    /// </summary>
    IReadOnlyList<AgentInstance> GetAllInstances();

    /// <summary>
    /// Stops all running agent instances. Called during Gateway shutdown.
    /// </summary>
    Task StopAllAsync(CancellationToken cancellationToken = default);
}
