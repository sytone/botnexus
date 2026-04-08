using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Api.Hubs;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<CronChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(provider => provider.GetRequiredService<CronChannelAdapter>());
        services.AddSingleton<SignalRChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(provider => provider.GetRequiredService<SignalRChannelAdapter>());
        services.AddControllers()
            .AddApplicationPart(typeof(GatewayApiServiceCollectionExtensions).Assembly);

        return services;
    }
}
