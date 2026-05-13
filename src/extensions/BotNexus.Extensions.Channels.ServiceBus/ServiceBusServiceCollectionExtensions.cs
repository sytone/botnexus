using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.ServiceBus;

/// <summary>
/// Dependency-injection extensions for registering the Azure Service Bus channel adapter.
/// </summary>
public static class ServiceBusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Service Bus channel adapter and its options binding.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">
    /// Optional inline configuration delegate. Called after any options already bound from
    /// <c>IConfiguration</c>, so it can override individual properties.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// To bind options from <c>IConfiguration</c>, call
    /// <c>services.Configure&lt;ServiceBusChannelOptions&gt;(config.GetSection("channels:servicebus"))</c>
    /// before or after this method. For managed-identity authentication, register a custom
    /// <see cref="IServiceBusAdapterClientFactory"/> as a singleton before calling this method.
    /// </remarks>
    public static IServiceCollection AddBotNexusServiceBusChannel(
        this IServiceCollection services,
        Action<ServiceBusChannelOptions>? configure = null)
    {
        services.AddOptions<ServiceBusChannelOptions>();

        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<IChannelAdapter, ServiceBusChannelAdapter>();
        return services;
    }
}
