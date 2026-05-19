using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Logging;
using BotNexus.Gateway.Api.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Extensions;

/// <summary>
/// DI registration for the Gateway API layer — controllers, triggers, logging.
/// Channel extensions (SignalR, etc.) are loaded dynamically by the extension loader.
/// </summary>
public static class GatewayApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Gateway API services (controllers, triggers, logging).
    /// Call after <c>AddBotNexusGateway()</c>.
    /// </summary>
    public static IServiceCollection AddBotNexusGatewayApi(this IServiceCollection services)
    {
        services.AddSingleton<IRecentLogStore, InMemoryRecentLogStore>();
        services.AddSingleton<ILoggerProvider>(serviceProvider =>
            new RecentLogEntryLoggerProvider(serviceProvider.GetRequiredService<IRecentLogStore>()));
        services.AddSingleton<CronTrigger>();
        services.AddSingleton<HeartbeatTrigger>();
        services.AddSingleton<SoulTrigger>();
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<CronTrigger>());
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<SoulTrigger>());
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<HeartbeatTrigger>());

        services.AddControllers()
            .AddApplicationPart(typeof(GatewayApiServiceCollectionExtensions).Assembly);

        return services;
    }
}
