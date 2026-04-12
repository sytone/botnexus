using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;

namespace BotNexus.Gateway.Tests.Contracts;

public sealed class AgentRegistryContractTests
{
    [Fact]
    public void IAgentRegistry_DefaultUpdateContract_ThrowsNotSupported()
    {
        IAgentRegistry registry = new MinimalRegistry();
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            Description = "test",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

        var action = () => registry.Update(AgentId.From("agent-a"), descriptor);

        action.Should().Throw<NotSupportedException>()
            .WithMessage("*does not support descriptor updates*");
    }

    private sealed class MinimalRegistry : IAgentRegistry
    {
        public void Register(AgentDescriptor descriptor) { }
        public void Unregister(AgentId agentId) { }
        public AgentDescriptor? Get(AgentId agentId) => null;
        public IReadOnlyList<AgentDescriptor> GetAll() => [];
        public bool Contains(AgentId agentId) => false;
    }
}
