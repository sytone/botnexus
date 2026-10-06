using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

public sealed class NoOpAgentConfigurationWriter : IAgentConfigurationWriter
{
    public Task SaveAsync(AgentDescriptor descriptor, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DeleteAsync(AgentId agentId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
