using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Registry for agent descriptors — the static configuration of what agents exist.
/// Agents are registered at startup (from config) or dynamically via the management API.
/// This is the "phone book" of agents; it does not manage running instances.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe. The Gateway reads from this registry
/// when routing messages and creating agent instances.
/// </remarks>
public interface IAgentRegistry
{
    /// <summary>
    /// Registers a new agent descriptor. Throws if an agent with the same ID already exists.
    /// </summary>
    /// <param name="descriptor">The agent descriptor to register.</param>
    /// <exception cref="InvalidOperationException">An agent with this ID is already registered.</exception>
    void Register(AgentDescriptor descriptor);

    /// <summary>
    /// Removes an agent registration. No-op if the agent is not registered.
    /// Running instances of this agent are <b>not</b> automatically stopped.
    /// </summary>
    /// <param name="agentId">The agent ID to unregister.</param>
    void Unregister(AgentId agentId);

    /// <summary>
    /// Updates an existing agent descriptor while preserving registration identity.
    /// </summary>
    /// <param name="agentId">The registered agent ID to update.</param>
    /// <param name="descriptor">The updated descriptor payload.</param>
    /// <returns><c>true</c> when updated; otherwise <c>false</c> if the agent does not exist.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="descriptor"/> uses a different agent ID.</exception>
    bool Update(AgentId agentId, AgentDescriptor descriptor)
        => throw new NotSupportedException("This registry does not support descriptor updates.");

    /// <summary>
    /// Gets a runtime-enabled agent descriptor by ID, or null if absent or disabled.
    /// </summary>
    AgentDescriptor? Get(AgentId agentId);

    /// <summary>
    /// Gets all runtime-enabled agent descriptors.
    /// </summary>
    IReadOnlyList<AgentDescriptor> GetAll();

    /// <summary>
    /// Gets a descriptor for administration when includeDisabled is true, including agents
    /// that cannot admit runtime work. Existing registry implementations may delegate to Get.
    /// </summary>
    AgentDescriptor? Get(AgentId agentId, bool includeDisabled) => Get(agentId);

    /// <summary>Lists descriptors, optionally retaining disabled registrations for administration.</summary>
    IReadOnlyList<AgentDescriptor> GetAll(bool includeDisabled) => GetAll();

    /// <summary>
    /// Checks whether an agent with the given ID is registered.
    /// </summary>
    bool Contains(AgentId agentId);
}
