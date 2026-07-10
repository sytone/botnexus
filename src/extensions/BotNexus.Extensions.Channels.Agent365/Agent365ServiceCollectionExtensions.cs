using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Extensions.Channels.Agent365;

/// <summary>
/// Dependency-injection extensions for registering the Agent 365 channel adapter.
/// </summary>
public static class Agent365ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Agent 365 channel adapter, its options binding, the outbound connector sender,
    /// and named HTTP client support used by the Agents SDK connector.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">Optional options configurator.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddBotNexusAgent365Channel(
        this IServiceCollection services,
        Action<Agent365GatewayOptions>? configure = null)
    {
        services.AddOptions<Agent365GatewayOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpClient();
        services.TryAddSingleton<IAgent365ConnectorSender, Agent365ConnectorSender>();
        services.AddSingleton<IChannelAdapter, Agent365ChannelAdapter>();
        return services;
    }
}
