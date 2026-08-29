using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.DebugTool;

/// <summary>
/// Contributes the <see cref="DebugTool"/> for each agent session.
/// The tool is always contributed (default tool) unless disabled via extension config.
/// </summary>
public sealed class DebugToolContributor : IAgentToolContributor
{
    private readonly string _dbPath;
    private readonly IRuntimeStateProvider? _runtimeStateProvider;
    private readonly ISecretRedactor? _secretRedactor;

    /// <summary>
    /// Creates a new contributor with the platform sessions database path.
    /// </summary>
    /// <param name="dbPath">Absolute path to the sessions.sqlite database file.</param>
    /// <param name="runtimeStateProvider">Optional runtime state provider for the runtime_status action.</param>
    /// <param name="secretRedactor">Optional secret redactor applied to query and runtime output so the debug surface cannot echo credentials.</param>
    public DebugToolContributor(
        string dbPath,
        IRuntimeStateProvider? runtimeStateProvider = null,
        ISecretRedactor? secretRedactor = null)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _runtimeStateProvider = runtimeStateProvider;
        _secretRedactor = secretRedactor;
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
        IReadOnlyList<IAgentTool> tools = [new DebugTool(_dbPath, agentId, config, _runtimeStateProvider, _secretRedactor)];

        return Task.FromResult(new AgentToolContribution(tools));
    }

    private static DebugToolConfig ResolveConfig(AgentDescriptor descriptor)
        // Absent or malformed config both fall back to defaults; the debug tool is diagnostic-only,
        // so a typo should not change what it exposes.
        => ExtensionConfigBinder.Bind<DebugToolConfig>(descriptor, "botnexus-debug-tool")
           ?? new DebugToolConfig();
}
