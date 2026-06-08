using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// Dependency-injection extensions for registering the test channel adapter.
/// </summary>
public static class TestChannelServiceCollectionExtensions
{
    /// <summary>
    /// Registers the test channel adapter as a singleton <see cref="IChannelAdapter"/>.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">Optional options configurator.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddBotNexusTestChannel(
        this IServiceCollection services,
        Action<TestChannelOptions>? configure = null)
    {
        services.AddOptions<TestChannelOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<TestChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<TestChannelAdapter>());
        return services;
    }
}
