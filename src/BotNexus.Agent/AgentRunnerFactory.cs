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
    private readonly IReadOnlyList<IChannel> _channels;
    private readonly ICommandRouter? _commandRouter;
    private readonly ICronService? _cronService;
    private readonly IActivityStream? _activityStream;

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
        IEnumerable<IChannel> channels,
        IBotNexusMetrics? metrics = null,
        ICommandRouter? commandRouter = null,
        ICronService? cronService = null,
        IActivityStream? activityStream = null)
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
        _channels = channels.ToList();
        _commandRouter = commandRouter;
        _cronService = cronService;
        _activityStream = activityStream;
    }

    /// <inheritdoc />
    public IAgentRunner Create(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var workspace = _agentWorkspaceFactory.Create(agentName);
        var contextBuilder = _contextBuilderFactory.Create(agentName);
        var agentConfig = ResolveAgentConfig(agentName);
        var defaults = _config.Value.Agents;
        var toolsConfig = _config.Value.Tools;

        var settings = new GenerationSettings
        {
            Model = agentConfig.Model ?? defaults.Model,
            MaxTokens = agentConfig.MaxTokens ?? defaults.MaxTokens,
            Temperature = agentConfig.Temperature ?? defaults.Temperature,
            ContextWindowTokens = defaults.ContextWindowTokens,
            MaxToolIterations = agentConfig.MaxToolIterations ?? defaults.MaxToolIterations
        };

        var logger = _loggerFactory.CreateLogger<AgentRunnerFactory>();
        logger.LogInformation("Creating agent '{AgentName}': configuredModel={ConfiguredModel}, maxTokens={MaxTokens}, contextWindowTokens={ContextWindowTokens}, temperature={Temperature}",
            agentName, settings.Model, settings.MaxTokens, settings.ContextWindowTokens, settings.Temperature);

        var toolRegistry = new ToolRegistry(_metrics);

        var disallowed = new HashSet<string>(agentConfig.DisallowedTools, StringComparer.OrdinalIgnoreCase);

        var internalTools = CreateInternalTools(workspace, toolsConfig);
        var allTools = internalTools.Concat(_tools)
            .Where(t => !disallowed.Contains(t.Definition.Name));

        var agentLoop = new AgentLoop(
            agentName: workspace.AgentName,
            providerRegistry: _providerRegistry,
            sessionManager: _sessionManager,
            contextBuilder: contextBuilder,
            toolRegistry: toolRegistry,
            settings: settings,
            model: agentConfig.Model,
            providerName: agentConfig.Provider,
            additionalTools: allTools,
            enableMemory: agentConfig.EnableMemory == true,
            memoryStore: _memoryStore,
            disallowedTools: disallowed,
            hooks: [.. _hooks],
            logger: _loggerFactory.CreateLogger<AgentLoop>(),
            metrics: _metrics,
            maxToolIterations: agentConfig.MaxToolIterations ?? defaults.MaxToolIterations,
            activityStream: _activityStream);

        return new AgentRunner(
            agentName: workspace.AgentName,
            agentLoop: agentLoop,
            logger: _loggerFactory.CreateLogger<AgentRunner>(),
            responseChannel: _channels.FirstOrDefault(),
            commandRouter: _commandRouter);
    }

    private IReadOnlyList<ITool> CreateInternalTools(IAgentWorkspace workspace, ToolsConfig toolsConfig)
    {
        var logger = _loggerFactory.CreateLogger<AgentRunnerFactory>();
        var tools = new List<ITool>
        {
            new FilesystemTool(workspace.WorkspacePath, toolsConfig.RestrictToWorkspace, logger),
            new WebTool(logger: logger),
            new MessageTool(_channels.FirstOrDefault(), logger),
            new CronTool(_cronService, this, _sessionManager, _channels, logger)
        };

        if (toolsConfig.Exec.Enable)
            tools.Add(new ShellTool(workspace.WorkspacePath, toolsConfig.Exec.Timeout, logger));

        return tools;
    }

    private AgentConfig ResolveAgentConfig(string agentName)
    {
        return _config.Value.Agents.Named.TryGetValue(agentName, out var config)
            ? config
            : new AgentConfig { Name = agentName };
    }
}
