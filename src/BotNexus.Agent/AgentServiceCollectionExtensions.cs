using BotNexus.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Agent;

/// <summary>
/// Extension methods for registering agent workspace/context services.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers workspace services for agent-specific workspace resolution.
    /// </summary>
    public static IServiceCollection AddAgentWorkspace(this IServiceCollection services)
    {
        services.AddTransient<IAgentWorkspaceFactory, AgentWorkspaceFactory>();
        services.AddTransient<IAgentWorkspace>(sp =>
            sp.GetRequiredService<IAgentWorkspaceFactory>().Create("default"));
        return services;
    }

    /// <summary>
    /// Registers context builder services for agent-specific context construction.
    /// </summary>
    public static IServiceCollection AddAgentContextBuilder(this IServiceCollection services)
    {
        services.AddAgentWorkspace();
        services.AddTransient<IContextBuilderFactory, AgentContextBuilderFactory>();
        services.AddTransient<IContextBuilder>(sp =>
            sp.GetRequiredService<IContextBuilderFactory>().Create("default"));
        return services;
    }
}
