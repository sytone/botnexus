using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Channels.Base;
using BotNexus.Cron;
using BotNexus.Heartbeat;
using BotNexus.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway;

/// <summary>Extension methods to register the full BotNexus stack.</summary>
public static class BotNexusServiceExtensions
{
    /// <summary>Registers all BotNexus services.</summary>
    public static IServiceCollection AddBotNexus(this IServiceCollection services, IConfiguration configuration)
    {
        // Core
        services.AddBotNexusCore(configuration);

        // Session
        services.AddBotNexusSession();

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
        services.AddSingleton<ICronService, CronService>();
        services.AddHostedService(sp => (CronService)sp.GetRequiredService<ICronService>());

        // Heartbeat
        services.AddSingleton<IHeartbeatService, HeartbeatService>();
        services.AddHostedService(sp => (HeartbeatService)sp.GetRequiredService<IHeartbeatService>());

        // Gateway
        services.AddHostedService<Gateway>();

        return services;
    }
}
