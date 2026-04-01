using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Agent;

public sealed class AgentContextBuilderFactory : IContextBuilderFactory
{
    private readonly IAgentWorkspaceFactory _workspaceFactory;
    private readonly IMemoryStore _memoryStore;
    private readonly IOptions<BotNexusConfig> _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly IBotNexusMetrics? _metrics;

    public AgentContextBuilderFactory(
        IAgentWorkspaceFactory workspaceFactory,
        IMemoryStore memoryStore,
        IOptions<BotNexusConfig> config,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        IBotNexusMetrics? metrics = null)
    {
        _workspaceFactory = workspaceFactory;
        _memoryStore = memoryStore;
        _config = config;
        _loggerFactory = loggerFactory;
        _services = services;
        _metrics = metrics;
    }

    public IContextBuilder Create(string agentName)
    {
        var workspace = _workspaceFactory.Create(agentName);
        var toolRegistry = new ToolRegistry(_metrics);
        toolRegistry.RegisterRange(_services.GetServices<ITool>());
        return new AgentContextBuilder(
            workspace,
            _memoryStore,
            toolRegistry,
            _config,
            _loggerFactory.CreateLogger<AgentContextBuilder>());
    }
}
