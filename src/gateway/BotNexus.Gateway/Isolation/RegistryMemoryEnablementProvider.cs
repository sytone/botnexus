using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Live <see cref="IMemoryEnablementProvider"/> backed by the agent registry (issue #3361).
/// </summary>
/// <remarks>
/// <para>
/// The registry is the same object the management API and the configuration source write agent
/// updates into, so reading <c>Get(agentId)?.Memory?.Enabled</c> on every tool invocation observes a
/// live disablement immediately - without the isolation strategy having to evict and rebuild a
/// cached handle, and without a second copy of the enablement state that could drift from the one
/// the operator actually edited.
/// </para>
/// <para>
/// <b>Fails closed on a missing agent.</b> If the descriptor has been unregistered the answer is
/// "disabled", not "unchanged". An unregistered agent is exactly the case where nobody is left to
/// own the store, so a retained handle must not keep writing to it.
/// </para>
/// </remarks>
internal sealed class RegistryMemoryEnablementProvider : IMemoryEnablementProvider
{
    private readonly IAgentRegistry _registry;
    private readonly AgentId _agentId;

    public RegistryMemoryEnablementProvider(IAgentRegistry registry, AgentId agentId)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _agentId = agentId;
    }

    /// <inheritdoc />
    public bool IsMemoryEnabled() => _registry.Get(_agentId)?.Memory?.Enabled == true;
}
