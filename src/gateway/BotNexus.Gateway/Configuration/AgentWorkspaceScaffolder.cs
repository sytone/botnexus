using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

internal sealed class AgentWorkspaceScaffolder(
    IAgentRegistry registry,
    BotNexusHome botNexusHome,
    ILogger<AgentWorkspaceScaffolder> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var agents = registry.GetAll();
        var scaffolded = 0;

        foreach (var descriptor in agents)
        {
            var agentId = descriptor.AgentId.Value;
            if (agentId.Contains("--subagent--", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var agentDir = botNexusHome.GetAgentDirectory(agentId);
                var workspacePath = System.IO.Path.Combine(agentDir, "workspace");
                logger.LogDebug("Workspace scaffold verified for agent ''{AgentId}'' at {WorkspacePath}.", agentId, workspacePath);
                scaffolded++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to scaffold workspace for agent ''{AgentId}''.", agentId);
            }
        }

        if (scaffolded > 0)
            logger.LogInformation("Workspace scaffold check complete: {Count} agent(s) verified.", scaffolded);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
