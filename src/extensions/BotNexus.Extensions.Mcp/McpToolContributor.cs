using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Contributes MCP-bridged tools by starting configured servers for the target agent session.
/// </summary>
public sealed class McpToolContributor(ILoggerFactory loggerFactory) : IAgentToolContributor
{
    /// <inheritdoc />
    public async Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        var config = ResolveExtensionConfig<McpExtensionConfig>(context.Descriptor, "botnexus-mcp");
        if (config is not { Servers.Count: > 0 })
            return new AgentToolContribution([]);

        var manager = new McpServerManager(loggerFactory.CreateLogger<McpServerManager>());
        var tools = await manager.StartServersAsync(config, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<object> resourcesToDispose = [manager];
        return new AgentToolContribution(tools, resourcesToDispose);
    }

    private static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId) where T : class
    {
        if (descriptor.ExtensionConfig.TryGetValue(extensionId, out var element))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(element.GetRawText());
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
