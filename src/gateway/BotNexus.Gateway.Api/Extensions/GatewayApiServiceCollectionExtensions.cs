using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Hubs;
using BotNexus.Gateway.Api.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Extensions;

/// <summary>
/// DI registration and endpoint mapping extensions for the Gateway API layer.
/// </summary>
public static class GatewayApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Gateway API services (controllers, SignalR hub + channel adapter).
    /// Call after <c>AddBotNexusGateway()</c>.
    /// </summary>
    public static IServiceCollection AddBotNexusGatewayApi(this IServiceCollection services)
    {
        services.AddSingleton<IRecentLogStore, InMemoryRecentLogStore>();
        services.AddSingleton<ILoggerProvider>(serviceProvider =>
            new RecentLogEntryLoggerProvider(serviceProvider.GetRequiredService<IRecentLogStore>()));
        services.AddSingleton<CronTrigger>();
        services.AddSingleton<SoulTrigger>();
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<CronTrigger>());
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<SoulTrigger>());
        services.AddSingleton<SignalRChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(provider => provider.GetRequiredService<SignalRChannelAdapter>());
        services.AddControllers()
            .AddApplicationPart(typeof(GatewayApiServiceCollectionExtensions).Assembly);

        return services;
    }
}
