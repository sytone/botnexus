using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Memory;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Contributes the <see cref="KnowledgeSearchTool"/> when
/// the QMD extension is enabled for the agent.
/// </summary>
public sealed class QmdToolContributor(
    ILoggerFactory? loggerFactory = null,
    ISharedMemoryStoreRegistry? memoryStoreRegistry = null) : IAgentToolContributor
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

        var logger = loggerFactory?.CreateLogger<QmdCliBackend>();
        var cliBackend = new QmdCliBackend(config.QmdPath, TimeSpan.FromSeconds(30), logger);

        IQmdBackend backend = BuildBackend(cliBackend, config, context.Descriptor.AgentId.Value);
        IReadOnlyList<IAgentTool> tools = [new KnowledgeSearchTool(backend, config), new KnowledgeStoresTool(backend, config), new KnowledgeGetTool(backend, config)];

        return Task.FromResult(new AgentToolContribution(tools, [backend]));
    }

    private IQmdBackend BuildBackend(QmdCliBackend cliBackend, QmdConfig config, string agentId)
    {
        if (!config.IncludeMemoryStores || memoryStoreRegistry is null)
            return cliBackend;

        var memoryBackend = new MemoryQmdBackend(memoryStoreRegistry, agentId);
        return new CompositeQmdBackend([cliBackend, memoryBackend]);
    }

    internal static QmdConfig ResolveConfig(AgentDescriptor descriptor)
    {
        if (descriptor.ExtensionConfig.TryGetValue("botnexus-qmd", out var element))
        {
            try
            {
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                return JsonSerializer.Deserialize<QmdConfig>(element.GetRawText(), options)
                       ?? new QmdConfig();
            }
            catch
            {
                return new QmdConfig();
            }
        }

        return new QmdConfig();
    }
}
