using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// Issue #3361: the enablement provider handed to the memory tools must read the agent registry
/// live, so an operator update to <c>memory.enabled</c> revokes tools on an already-created handle.
/// </summary>
public sealed class RegistryMemoryEnablementProviderTests
{
    [Fact]
    public void IsMemoryEnabled_ReflectsRegistryUpdateAfterConstruction()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var agentId = AgentId.From("agent-a");
        registry.Register(Descriptor(agentId, memoryEnabled: true));

        // Bound while enabled - this is the object the cached handle's tools keep hold of.
        var provider = new RegistryMemoryEnablementProvider(registry, agentId);
        provider.IsMemoryEnabled().ShouldBeTrue();

        registry.Update(agentId, Descriptor(agentId, memoryEnabled: false)).ShouldBeTrue();

        provider.IsMemoryEnabled().ShouldBeFalse();
    }

    [Fact]
    public void IsMemoryEnabled_ReflectsReEnablement()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var agentId = AgentId.From("agent-a");
        registry.Register(Descriptor(agentId, memoryEnabled: false));
        var provider = new RegistryMemoryEnablementProvider(registry, agentId);
        provider.IsMemoryEnabled().ShouldBeFalse();

        registry.Update(agentId, Descriptor(agentId, memoryEnabled: true)).ShouldBeTrue();

        provider.IsMemoryEnabled().ShouldBeTrue();
    }

    /// <summary>
    /// Fails closed: an unregistered agent has nobody left to own its store, so a retained handle
    /// must not keep writing to it.
    /// </summary>
    [Fact]
    public void IsMemoryEnabled_WhenAgentUnregistered_FailsClosed()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var agentId = AgentId.From("agent-a");
        registry.Register(Descriptor(agentId, memoryEnabled: true));
        var provider = new RegistryMemoryEnablementProvider(registry, agentId);

        registry.Unregister(agentId);

        provider.IsMemoryEnabled().ShouldBeFalse();
    }

    /// <summary>
    /// A descriptor with no memory block at all is disabled, not "unspecified therefore allowed".
    /// </summary>
    [Fact]
    public void IsMemoryEnabled_WhenDescriptorHasNoMemoryBlock_IsDisabled()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var agentId = AgentId.From("agent-a");
        registry.Register(new AgentDescriptor
        {
            AgentId = agentId,
            DisplayName = "Agent A",
            ModelId = "model-a",
            ApiProvider = "provider-a",
            Memory = null
        });

        new RegistryMemoryEnablementProvider(registry, agentId).IsMemoryEnabled().ShouldBeFalse();
    }

    private static AgentDescriptor Descriptor(AgentId agentId, bool memoryEnabled) => new()
    {
        AgentId = agentId,
        DisplayName = "Agent A",
        ModelId = "model-a",
        ApiProvider = "provider-a",
        Memory = new MemoryAgentConfig { Enabled = memoryEnabled }
    };
}
