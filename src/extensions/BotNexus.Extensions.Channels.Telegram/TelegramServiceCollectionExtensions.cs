using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Dependency-injection extensions for registering the Telegram channel adapter.
/// </summary>
public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Telegram channel adapter, options binding, and named HTTP client support
    /// used to create one Telegram API client per configured bot.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">Optional options configurator.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddBotNexusTelegramChannel(
        this IServiceCollection services,
        Action<TelegramGatewayOptions>? configure = null)
    {
        services.AddOptions<TelegramGatewayOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpClient();
        services.AddSingleton<IChannelAdapter, TelegramChannelAdapter>();
        return services;
    }
}
