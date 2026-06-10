using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Registers built-in internal agents at startup before configuration-based agents load.
/// This ensures they appear in <see cref="AgentConfigurationHostedService"/>'s
/// <c>_codeBasedAgentIds</c> set and cannot be overridden by user configuration.
/// </summary>
/// <remarks>
/// Must be registered before <see cref="AgentConfigurationHostedService"/> in the hosted
/// service pipeline so that <see cref="IAgentRegistry.GetAll"/> returns these agents
/// when the configuration service snapshots code-based IDs.
/// </remarks>
internal sealed class BuiltInAgentRegistrationService(
    IAgentRegistry registry,
    ILogger<BuiltInAgentRegistrationService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var registered = 0;
        foreach (var descriptor in BuiltInAgents.All)
        {
            if (registry.Contains(descriptor.AgentId))
            {
                logger.LogDebug(
                    "Built-in agent '{AgentId}' already registered, skipping.",
                    descriptor.AgentId);
                continue;
            }

            registry.Register(descriptor);
            registered++;
        }

        logger.LogInformation(
            "Registered {Count} built-in agents: {AgentIds}",
            registered,
            string.Join(", ", BuiltInAgents.All.Select(a => a.AgentId.Value)));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
