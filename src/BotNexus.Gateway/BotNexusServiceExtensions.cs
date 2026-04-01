using BotNexus.Agent;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Extensions;
using BotNexus.Channels.Base;
using BotNexus.Cron;
using BotNexus.Cron.Actions;
using BotNexus.Gateway.HealthChecks;
using BotNexus.Core.Configuration;
using BotNexus.Session;
using BotNexus.Providers.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway;

/// <summary>Extension methods to register the full BotNexus stack.</summary>
public static class BotNexusServiceExtensions
{
    /// <summary>Registers all BotNexus services.</summary>
    public static IServiceCollection AddBotNexus(this IServiceCollection services, IConfiguration configuration)
    {
        // Core
        services.AddBotNexusCore(configuration);
        services.AddAgentContextBuilder();
        services.AddBotNexusExtensions(configuration);
        services.AddSingleton<ProviderRegistry>(sp =>
        {
            var providers = sp.GetServices<ILlmProvider>().ToList();
            var registrationKeys = sp.GetServices<ExtensionServiceRegistration>()
                .Where(r => r.ServiceType == typeof(ILlmProvider))
                .Select(r => r.Key)
                .ToList();

            var registry = new ProviderRegistry(providers);
            var registrationsToApply = Math.Min(providers.Count, registrationKeys.Count);
            for (var i = 0; i < registrationsToApply; i++)
                registry.Register(registrationKeys[i], providers[i]);

            return registry;
        });

        // Session
        services.AddBotNexusSession();
        services.AddSingleton<IMemoryStore>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<BotNexusConfig>>().Value;
            var legacyBasePath = BotNexusHome.ResolvePath(config.Agents.Workspace);
            var logger = sp.GetRequiredService<ILogger<MemoryStore>>();
            return new MemoryStore(legacyBasePath, logger);
        });
        services.AddSingleton<IMemoryConsolidator, MemoryConsolidator>();

        // WebSocket channel (singleton registered as both concrete type and IChannel so the
        // handler can manage connections while AgentRunner can route responses through it)
        services.AddSingleton<WebSocketChannel>();
        services.AddSingleton<IChannel>(sp => sp.GetRequiredService<WebSocketChannel>());
        services.AddSingleton<GatewayWebSocketHandler>();

        // Channel manager (includes WebSocketChannel; external channels added by channel extensions)
        services.AddSingleton<ChannelManager>(sp =>
        {
            var channels = sp.GetServices<IChannel>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ChannelManager>>();
            return new ChannelManager(channels, logger);
        });

        // Cron
        services.Configure<CronConfig>(configuration.GetSection($"{BotNexusConfig.SectionName}:Cron"));
        services.AddSingleton<ISystemAction, CheckUpdatesAction>();
        services.AddSingleton<ISystemAction, HealthAuditAction>();
        services.AddSingleton<ISystemAction, ExtensionScanAction>();
        services.AddSingleton<CronJobFactory>();
        services.AddHostedService<CronJobRegistrationHostedService>();
        services.AddSingleton<ICronService, CronService>();
        services.AddHostedService(sp => (CronService)sp.GetRequiredService<ICronService>());

        // Heartbeat (backward-compatible facade over cron)
        services.AddSingleton<IHeartbeatService, CronHeartbeatAdapter>();

        // Gateway
        services.AddSingleton<IAgentRunnerFactory, AgentRunnerFactory>();
        services.AddSingleton<IAgentRouter, AgentRouter>();
        services.AddHostedService<Gateway>();
        services.AddHealthChecks()
            .AddCheck<MessageBusHealthCheck>("message_bus")
            .AddCheck<ProviderRegistrationHealthCheck>("provider_registration")
            .AddCheck<ExtensionLoaderHealthCheck>("extension_loader")
            .AddCheck<ChannelReadinessHealthCheck>("channel_readiness", tags: ["ready"])
            .AddCheck<ProviderReadinessHealthCheck>("provider_readiness", tags: ["ready"])
            .AddCheck<CronServiceHealthCheck>("cron_service");

        return services;
    }
}
