using BotNexus.Gateway.Abstractions.Models;

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
    void Unregister(string agentId);

    /// <summary>
    /// Gets an agent descriptor by ID, or <c>null</c> if not registered.
    /// </summary>
    AgentDescriptor? Get(string agentId);

    /// <summary>
    /// Gets all registered agent descriptors.
    /// </summary>
    IReadOnlyList<AgentDescriptor> GetAll();

    /// <summary>
    /// Checks whether an agent with the given ID is registered.
    /// </summary>
    bool Contains(string agentId);
}
