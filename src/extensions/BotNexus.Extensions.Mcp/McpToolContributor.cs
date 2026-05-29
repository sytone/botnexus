using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Contributes MCP-bridged tools from the non-blocking server warmup cache.
/// </summary>
public sealed class McpToolContributor(ILoggerFactory loggerFactory) : IAgentToolContributor
{
    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = ResolveMcpExtensionConfig(context.Descriptor);
        if (config is not { Servers.Count: > 0 })
            return Task.FromResult(new AgentToolContribution([]));

        var entry = McpServerWarmupCache.EnsureStarted(
            context.Descriptor.AgentId.Value,
            config,
            loggerFactory.CreateLogger<McpServerManager>());

        var contribution = entry.TryGetReadyTools(out var tools)
            ? new AgentToolContribution(tools)
            : new AgentToolContribution([]);

        return Task.FromResult(contribution);
    }

    internal static McpExtensionConfig? ResolveMcpExtensionConfig(AgentDescriptor descriptor)
        => ResolveExtensionConfig<McpExtensionConfig>(descriptor, "botnexus-mcp");

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
