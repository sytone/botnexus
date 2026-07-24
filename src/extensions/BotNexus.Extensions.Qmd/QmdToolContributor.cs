using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Contributes the QMD knowledge tools (<c>knowledge_search</c>, <c>knowledge_stores</c>,
/// <c>knowledge_get</c>) when the QMD extension is <em>explicitly</em> enabled for the agent.
/// </summary>
/// <remarks>
/// QMD is opt-in (issue #2116). The contributor fails closed: absent, empty, or malformed
/// <c>botnexus-qmd</c> configuration yields a disabled config, so no tools are contributed and
/// no backend/indexing resources are created. Only an explicit
/// <c>extensions.botnexus-qmd.enabled: true</c> activates the extension.
/// </remarks>
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

        var config = ResolveConfig(context.Descriptor, loggerFactory?.CreateLogger<QmdToolContributor>());

        // Fail closed: without an explicit enabled:true, contribute nothing and start no
        // backend/indexing resources.
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

    /// <summary>
    /// Resolves the QMD configuration for an agent, failing closed to disabled.
    /// </summary>
    /// <param name="descriptor">The agent descriptor whose extension config is inspected.</param>
    /// <param name="logger">Optional logger used to emit diagnostics for malformed config.</param>
    /// <remarks>
    /// QMD is opt-in (issue #2116). This method never returns an enabled config unless the agent
    /// supplied a well-formed <c>botnexus-qmd</c> block with <c>enabled: true</c>:
    /// <list type="bullet">
    ///   <item>Missing config =&gt; disabled (a fresh <see cref="QmdConfig"/> whose default is disabled).</item>
    ///   <item>Empty/partial config without <c>enabled</c> =&gt; disabled (inherits the default).</item>
    ///   <item>Malformed config (not a JSON object, wrong shape) =&gt; disabled, with a warning
    ///   diagnostic identifying the offending <c>botnexus-qmd</c> block.</item>
    /// </list>
    /// </remarks>
    internal static QmdConfig ResolveConfig(AgentDescriptor descriptor, ILogger? logger = null)
    {
        if (!descriptor.ExtensionConfig.TryGetValue("botnexus-qmd", out var element))
        {
            // No QMD config present: fail closed to disabled.
            return new QmdConfig();
        }

        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var parsed = JsonSerializer.Deserialize<QmdConfig>(element.GetRawText(), options);
            if (parsed is null)
            {
                (logger ?? NullLogger.Instance).LogWarning(
                    "QMD extension config 'botnexus-qmd' for agent '{AgentId}' deserialized to null; failing closed to disabled.",
                    descriptor.AgentId.Value);
                return new QmdConfig();
            }

            return parsed;
        }
        catch (JsonException ex)
        {
            // Malformed config must fail closed to disabled, not enabled. Emit useful diagnostics.
            (logger ?? NullLogger.Instance).LogWarning(
                ex,
                "QMD extension config 'botnexus-qmd' for agent '{AgentId}' is malformed and was ignored; failing closed to disabled. Fix the extension config or remove it. Raw JSON kind: {ValueKind}.",
                descriptor.AgentId.Value,
                element.ValueKind);
            return new QmdConfig();
        }
    }
}
