using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Mcp;

public sealed class McpServerWarmupHostedService(
    IAgentRegistry agentRegistry,
    ILoggerFactory loggerFactory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var logger = loggerFactory.CreateLogger<McpServerManager>();
        foreach (var descriptor in agentRegistry.GetAll())
        {
            var config = McpToolContributor.ResolveMcpExtensionConfig(descriptor);
            if (config is not { Servers.Count: > 0 })
                continue;

            McpServerWarmupCache.EnsureStarted(descriptor.AgentId.Value, config, logger);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await McpServerWarmupCache.DisposeAllAsync().ConfigureAwait(false);
    }
}
