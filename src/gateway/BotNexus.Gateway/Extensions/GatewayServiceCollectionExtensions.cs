using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Activity;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// DI registration extensions for the Gateway runtime services.
/// </summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core Gateway services: registry, supervisor, router, broadcaster,
    /// in-process isolation strategy, and the Gateway host background service.
    /// </summary>
    public static IServiceCollection AddBotNexusGateway(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<IAgentRegistry, DefaultAgentRegistry>();
        services.AddSingleton<IAgentSupervisor, DefaultAgentSupervisor>();
        services.AddSingleton<DefaultMessageRouter>();
        services.AddSingleton<IMessageRouter>(sp => sp.GetRequiredService<DefaultMessageRouter>());
        services.AddSingleton<IActivityBroadcaster, InMemoryActivityBroadcaster>();

        // Default isolation strategy
        services.AddSingleton<IIsolationStrategy, InProcessIsolationStrategy>();

        // Gateway host
        services.AddHostedService<GatewayHost>();

        return services;
    }
}
