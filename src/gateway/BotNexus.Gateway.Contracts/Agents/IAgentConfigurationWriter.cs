using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Persists agent descriptors to an external configuration store.
/// </summary>
public interface IAgentConfigurationWriter
{
    /// <summary>
    /// Persists the descriptor for an agent.
    /// </summary>
    /// <param name="descriptor">Descriptor to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(AgentDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes persisted configuration for an agent.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(AgentId agentId, CancellationToken cancellationToken = default);
}
