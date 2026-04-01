using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Core.Observability;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Agent;

/// <summary>
/// Creates configured <see cref="IAgentRunner"/> instances for named agents.
/// </summary>
public sealed class AgentRunnerFactory : IAgentRunnerFactory
{
    private readonly IContextBuilderFactory _contextBuilderFactory;
    private readonly IAgentWorkspaceFactory _agentWorkspaceFactory;
    private readonly ProviderRegistry _providerRegistry;
    private readonly ISessionManager _sessionManager;
    private readonly IOptions<BotNexusConfig> _config;
    private readonly IEnumerable<ITool> _tools;
    private readonly IMemoryStore _memoryStore;
    private readonly IEnumerable<IAgentHook> _hooks;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IBotNexusMetrics? _metrics;

    public AgentRunnerFactory(
        IContextBuilderFactory contextBuilderFactory,
        IAgentWorkspaceFactory agentWorkspaceFactory,
        ProviderRegistry providerRegistry,
        ISessionManager sessionManager,
        IOptions<BotNexusConfig> config,
        IEnumerable<ITool> tools,
        IMemoryStore memoryStore,
        IEnumerable<IAgentHook> hooks,
        ILoggerFactory loggerFactory,
        IBotNexusMetrics? metrics = null)
    {
        _contextBuilderFactory = contextBuilderFactory;
        _agentWorkspaceFactory = agentWorkspaceFactory;
        _providerRegistry = providerRegistry;
        _sessionManager = sessionManager;
        _config = config;
        _tools = tools;
        _memoryStore = memoryStore;
        _hooks = hooks;
        _loggerFactory = loggerFactory;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public IAgentRunner Create(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var workspace = _agentWorkspaceFactory.Create(agentName);
        var contextBuilder = _contextBuilderFactory.Create(agentName);
        var agentConfig = ResolveAgentConfig(agentName);
        var defaults = _config.Value.Agents;

        var settings = new GenerationSettings
        {
            Model = agentConfig.Model ?? defaults.Model,
            MaxTokens = agentConfig.MaxTokens ?? defaults.MaxTokens,
            Temperature = agentConfig.Temperature ?? defaults.Temperature,
            ContextWindowTokens = defaults.ContextWindowTokens,
            MaxToolIterations = agentConfig.MaxToolIterations ?? defaults.MaxToolIterations
        };

        var toolRegistry = new ToolRegistry(_metrics);

        var agentLoop = new AgentLoop(
            agentName: workspace.AgentName,
            providerRegistry: _providerRegistry,
            sessionManager: _sessionManager,
            contextBuilder: contextBuilder,
            toolRegistry: toolRegistry,
            settings: settings,
            model: agentConfig.Model,
            providerName: agentConfig.Provider,
            additionalTools: _tools,
            enableMemory: agentConfig.EnableMemory == true,
            memoryStore: _memoryStore,
            hooks: [.. _hooks],
            logger: _loggerFactory.CreateLogger<AgentLoop>(),
            metrics: _metrics,
            maxToolIterations: agentConfig.MaxToolIterations ?? defaults.MaxToolIterations);

        return new AgentRunner(
            agentName: workspace.AgentName,
            agentLoop: agentLoop,
            logger: _loggerFactory.CreateLogger<AgentRunner>());
    }

    private AgentConfig ResolveAgentConfig(string agentName)
    {
        return _config.Value.Agents.Named.TryGetValue(agentName, out var config)
            ? config
            : new AgentConfig { Name = agentName };
    }
}
