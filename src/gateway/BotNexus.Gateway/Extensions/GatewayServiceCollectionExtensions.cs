using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Activity;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Routing;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Security;
using BotNexus.Channels.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public static IServiceCollection AddBotNexusGateway(this IServiceCollection services, Action<GatewayOptions>? configure = null)
    {
        services.AddOptions<GatewayOptions>();
        if (configure is not null)
            services.Configure(configure);

        // Core services
        services.AddSingleton<IAgentRegistry, DefaultAgentRegistry>();
        services.AddSingleton<IAgentSupervisor, DefaultAgentSupervisor>();
        services.AddSingleton<IAgentCommunicator, DefaultAgentCommunicator>();
        services.AddSingleton<IMessageRouter, DefaultMessageRouter>();
        services.TryAddSingleton<ChannelManager>();
        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<IActivityBroadcaster, InMemoryActivityBroadcaster>();
        services.AddSingleton<IGatewayAuthHandler, ApiKeyGatewayAuthHandler>();

        // Default isolation strategy
        services.AddSingleton<IIsolationStrategy, InProcessIsolationStrategy>();

        // Gateway host
        services.AddHostedService<GatewayHost>();

        return services;
    }

    /// <summary>
    /// Sets the default routed agent through options configuration.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="agentId">Default agent ID to route to.</param>
    public static IServiceCollection SetDefaultAgent(this IServiceCollection services, string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        services.PostConfigure<GatewayOptions>(options => options.DefaultAgentId = agentId);
        return services;
    }
}
