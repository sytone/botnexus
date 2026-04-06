using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Channels.WebSocket;

public static class WebSocketServiceCollectionExtensions
{
    public static IServiceCollection AddBotNexusWebSocketChannel(this IServiceCollection services)
    {
        services.TryAddSingleton<WebSocketChannelAdapter>();
        services.TryAddSingleton<IGatewayWebSocketChannelAdapter>(provider => provider.GetRequiredService<WebSocketChannelAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelAdapter, WebSocketChannelAdapter>());

        return services;
    }
}
