using BotNexus.Gateway.Abstractions.Channels;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    /// <remarks>
    /// Registers <see cref="InMemorySessionStore"/> as the default <see cref="ISessionStore"/> via
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton(IServiceCollection, Type, Type)"/>.
    /// Consumers can replace it by registering their own <see cref="ISessionStore"/> implementation
    /// before or after calling this method.
    /// </remarks>
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
        services.TryAddSingleton<IChannelManager, ChannelManager>();
        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<IActivityBroadcaster, InMemoryActivityBroadcaster>();
        services.AddSingleton<IGatewayAuthHandler, ApiKeyGatewayAuthHandler>();

        // Default isolation strategy
        services.AddSingleton<IIsolationStrategy, InProcessIsolationStrategy>();

        // Gateway host
        services.AddHostedService<GatewayHost>();

        var defaultAgentConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "agents"));
        if (Directory.Exists(defaultAgentConfigPath) &&
            services.All(descriptor => descriptor.ServiceType != typeof(IAgentConfigurationSource)))
        {
            services.AddFileAgentConfiguration(defaultAgentConfigPath);
        }

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

    /// <summary>
    /// Registers an agent configuration source and ensures configuration-driven agent loading is hosted.
    /// </summary>
    /// <typeparam name="T">The configuration source type.</typeparam>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddAgentConfigurationSource<T>(this IServiceCollection services)
        where T : class, IAgentConfigurationSource
    {
        services.AddSingleton<IAgentConfigurationSource, T>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentConfigurationHostedService>());
        return services;
    }

    /// <summary>
    /// Registers a file-based agent configuration source.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="path">Directory containing agent configuration files.</param>
    public static IServiceCollection AddFileAgentConfiguration(this IServiceCollection services, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        services.AddSingleton<IAgentConfigurationSource>(serviceProvider =>
        {
            var hostEnvironment = serviceProvider.GetService<IHostEnvironment>();
            var resolvedPath = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(hostEnvironment?.ContentRootPath ?? AppContext.BaseDirectory, path));

            return new FileAgentConfigurationSource(
                resolvedPath,
                serviceProvider.GetRequiredService<ILogger<FileAgentConfigurationSource>>());
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentConfigurationHostedService>());
        return services;
    }
}
