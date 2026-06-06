using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.DataStore;

/// <summary>
/// Contributes the session-scoped <see cref="DataStoreTool"/> when
/// <see cref="DataStoreConfig.Enabled"/> is <c>true</c> for the agent.
/// </summary>
public sealed class DataStoreToolContributor : IAgentToolContributor
{
    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = ResolveConfig(context.Descriptor);

        if (!config.Enabled)
            return Task.FromResult(new AgentToolContribution(Array.Empty<IAgentTool>()));

        var storePath = Path.Combine(context.WorkspacePath, ".store", "agent-data.db");
        var backend   = new SqliteDataStoreBackend(storePath, config.MaxSizeBytes);
        IReadOnlyList<IAgentTool> tools = [new DataStoreTool(backend)];

        return Task.FromResult(new AgentToolContribution(tools, [backend]));
    }

    private static DataStoreConfig ResolveConfig(AgentDescriptor descriptor)
    {
        if (descriptor.ExtensionConfig.TryGetValue("botnexus-data-store", out var element))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<DataStoreConfig>(element.GetRawText())
                       ?? new DataStoreConfig();
            }
            catch
            {
                return new DataStoreConfig();
            }
        }

        return new DataStoreConfig();
    }
}
