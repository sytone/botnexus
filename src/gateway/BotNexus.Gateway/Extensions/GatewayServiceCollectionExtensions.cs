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
using BotNexus.Channels.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// DI registration extensions for the Gateway runtime services.
/// </summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core Gateway services: registry, supervisor, router, broadcaster,
    /// registered isolation strategies, and the Gateway host background service.
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
        services.AddOptions<SessionCleanupOptions>();
        if (configure is not null)
            services.Configure(configure);

        // Core services
        services.TryAddSingleton<BotNexusHome>();
        services.AddSingleton<IAgentWorkspaceManager, FileAgentWorkspaceManager>();
        services.AddSingleton<IContextBuilder, WorkspaceContextBuilder>();
        services.AddSingleton<IAgentRegistry, DefaultAgentRegistry>();
        services.AddSingleton<IAgentSupervisor, DefaultAgentSupervisor>();
        services.AddSingleton<IAgentCommunicator, DefaultAgentCommunicator>();
        services.AddSingleton<IMessageRouter, DefaultMessageRouter>();
        services.TryAddSingleton<IChannelManager, ChannelManager>();
        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<IActivityBroadcaster, InMemoryActivityBroadcaster>();
        services.AddSingleton<IGatewayAuthHandler, ApiKeyGatewayAuthHandler>();
        services.AddBotNexusWebSocketChannel();

        // Built-in isolation strategies
        services.AddSingleton<IIsolationStrategy, InProcessIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, SandboxIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, ContainerIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, RemoteIsolationStrategy>();

        // Gateway host
        services.AddHostedService<GatewayHost>();
        services.AddHostedService<SessionCleanupService>();

        var defaultAgentConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "agents"));
        if (Directory.Exists(defaultAgentConfigPath) &&
            services.All(descriptor => descriptor.ServiceType != typeof(IAgentConfigurationSource)))
        {
            services.AddFileAgentConfiguration(defaultAgentConfigPath);
        }

        return services;
    }

    /// <summary>
    /// Loads platform configuration from <c>~/.botnexus/config.json</c> and maps supported settings
    /// into Gateway service registration.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configPath">Optional explicit path to platform config.</param>
    public static IServiceCollection AddPlatformConfiguration(this IServiceCollection services, string? configPath = null)
    {
        var resolvedConfigPath = string.IsNullOrWhiteSpace(configPath)
            ? PlatformConfigLoader.DefaultConfigPath
            : Path.GetFullPath(configPath);
        var configDirectory = Path.GetDirectoryName(resolvedConfigPath) ?? PlatformConfigLoader.DefaultConfigDirectory;

        PlatformConfigLoader.EnsureConfigDirectory(configDirectory);
        var config = PlatformConfigLoader.Load(resolvedConfigPath);
        services.AddOptions<PlatformConfig>()
            .Configure(options => ApplyPlatformConfig(options, config));
        services.Replace(ServiceDescriptor.Singleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<PlatformConfig>>().Value));
        services.TryAddSingleton<GatewayAuthManager>();
        services.Replace(ServiceDescriptor.Singleton<IGatewayAuthHandler>(serviceProvider =>
            new ApiKeyGatewayAuthHandler(
                serviceProvider.GetRequiredService<IOptions<PlatformConfig>>().Value,
                serviceProvider.GetRequiredService<ILogger<ApiKeyGatewayAuthHandler>>())));

        var defaultAgentId = config.GetDefaultAgentId();
        if (!string.IsNullOrWhiteSpace(defaultAgentId))
        {
            services.PostConfigure<GatewayOptions>(options => options.DefaultAgentId = defaultAgentId);
        }

        ConfigureSessionStore(services, config, configDirectory);

        var agentsDirectory = config.GetAgentsDirectory();
        if (!string.IsNullOrWhiteSpace(agentsDirectory))
        {
            var agentsPath = ResolveConfiguredPath(configDirectory, agentsDirectory);
            services.RemoveAll<IAgentConfigurationSource>();
            services.AddFileAgentConfiguration(agentsPath);
        }

        services.AddSingleton<IAgentConfigurationSource>(serviceProvider =>
            new PlatformConfigAgentSource(
                serviceProvider.GetRequiredService<IOptions<PlatformConfig>>(),
                configDirectory,
                serviceProvider.GetRequiredService<ILogger<PlatformConfigAgentSource>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentConfigurationHostedService>());

        return services;
    }

    private static string ResolveConfiguredPath(string configDirectory, string configuredPath)
        => Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(configDirectory, configuredPath));

    private static void ApplyPlatformConfig(PlatformConfig target, PlatformConfig source)
    {
        target.Gateway = source.Gateway;
        target.Agents = source.Agents;
        target.Providers = source.Providers;
        target.Channels = source.Channels;
        target.ApiKey = source.ApiKey;
        target.ApiKeys = source.ApiKeys;
        target.ListenUrl = source.ListenUrl;
        target.DefaultAgentId = source.DefaultAgentId;
        target.AgentsDirectory = source.AgentsDirectory;
        target.SessionsDirectory = source.SessionsDirectory;
        target.SessionStore = source.SessionStore;
        target.LogLevel = source.LogLevel;
    }

    private static void ConfigureSessionStore(IServiceCollection services, PlatformConfig config, string configDirectory)
    {
        var sessionStore = config.GetSessionStore();
        var explicitType = sessionStore?.Type?.Trim();
        var sessionsDirectory = config.GetSessionsDirectory();
        var resolvedType = !string.IsNullOrWhiteSpace(explicitType)
            ? explicitType
            : !string.IsNullOrWhiteSpace(sessionsDirectory)
                ? "File"
                : "InMemory";

        if (resolvedType.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.Replace(ServiceDescriptor.Singleton<ISessionStore, InMemorySessionStore>());
            return;
        }

        if (resolvedType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var configuredPath = sessionStore?.FilePath ?? sessionsDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.filePath is required when gateway.sessionStore.type is 'File'."]);

            var sessionsPath = ResolveConfiguredPath(configDirectory, configuredPath);
            Directory.CreateDirectory(sessionsPath);
            services.Replace(ServiceDescriptor.Singleton<ISessionStore>(serviceProvider =>
                new FileSessionStore(
                    sessionsPath,
                    serviceProvider.GetRequiredService<ILogger<FileSessionStore>>())));
            return;
        }

        throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.type must be either 'InMemory' or 'File'."]);
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
