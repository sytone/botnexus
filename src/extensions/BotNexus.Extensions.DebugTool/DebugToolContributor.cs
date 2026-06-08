using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.DebugTool;

/// <summary>
/// Contributes the <see cref="DebugTool"/> for each agent session.
/// The tool is always contributed (default tool) unless disabled via extension config.
/// </summary>
public sealed class DebugToolContributor : IAgentToolContributor
{
    private readonly string _dbPath;
    private readonly IRuntimeStateProvider? _runtimeStateProvider;

    /// <summary>
    /// Creates a new contributor with the platform sessions database path.
    /// </summary>
    /// <param name="dbPath">Absolute path to the sessions.sqlite database file.</param>
    /// <param name="runtimeStateProvider">Optional runtime state provider for the runtime_status action.</param>
    public DebugToolContributor(string dbPath, IRuntimeStateProvider? runtimeStateProvider = null)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _runtimeStateProvider = runtimeStateProvider;
    }

    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = ResolveConfig(context.Descriptor);

        if (!config.Enabled)
            return Task.FromResult(new AgentToolContribution(Array.Empty<IAgentTool>()));

        var agentId = context.Descriptor.AgentId.Value;
        IReadOnlyList<IAgentTool> tools = [new DebugTool(_dbPath, agentId, config, _runtimeStateProvider)];

        return Task.FromResult(new AgentToolContribution(tools));
    }

    private static DebugToolConfig ResolveConfig(AgentDescriptor descriptor)
    {
        if (descriptor.ExtensionConfig.TryGetValue("botnexus-debug-tool", out var element))
        {
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return System.Text.Json.JsonSerializer.Deserialize<DebugToolConfig>(element.GetRawText(), options)
                       ?? new DebugToolConfig();
            }
            catch
            {
                return new DebugToolConfig();
            }
        }

        return new DebugToolConfig();
    }
}
