using BotNexus.AgentCore.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// DI registration extensions for the built-in agent tools.
/// </summary>
public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in tools and tool registry.
    /// </summary>
    public static IServiceCollection AddBotNexusTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolFactory, DefaultAgentToolFactory>();

        // Tool registry collects extension IAgentTool registrations.
        services.AddSingleton<IToolRegistry>(sp => new DefaultToolRegistry(sp.GetServices<IAgentTool>()));

        return services;
    }
}
