using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Channels.Telegram;

/// <summary>
/// Dependency-injection extensions for registering the Telegram channel adapter.
/// </summary>
public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Telegram channel adapter and configuration options.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">Optional options configurator.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// Phase 2 stub registration for host integration. A full implementation would
    /// also register Telegram API client dependencies.
    /// </remarks>
    public static IServiceCollection AddBotNexusTelegramChannel(
        this IServiceCollection services,
        Action<TelegramOptions>? configure = null)
    {
        services.AddOptions<TelegramOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddSingleton<IChannelAdapter, TelegramChannelAdapter>();

        return services;
    }
}
