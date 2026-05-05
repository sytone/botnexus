using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.McpInvoke;

/// <summary>
/// Contributes the <see cref="McpInvokeTool"/> based on per-agent extension configuration.
/// </summary>
public sealed class McpInvokeToolContributor(ILoggerFactory loggerFactory) : IAgentToolContributor
{
    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = ResolveExtensionConfig<McpInvokeConfig>(context.Descriptor, "botnexus-mcp-invoke");
        if (config is not { Enabled: true, Servers.Count: > 0 })
            return Task.FromResult(new AgentToolContribution([]));

        IReadOnlyList<IAgentTool> tools =
        [
            new McpInvokeTool(config, loggerFactory.CreateLogger<McpInvokeTool>())
        ];

        return Task.FromResult(new AgentToolContribution(tools));
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
