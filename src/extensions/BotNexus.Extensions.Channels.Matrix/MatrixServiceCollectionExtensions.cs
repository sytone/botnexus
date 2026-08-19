using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Dependency-injection extensions for registering the Matrix channel adapter.
/// </summary>
public static class MatrixServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Matrix channel adapter, its options binding, and the default HTTP-backed
    /// Matrix client factory.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">
    /// Optional inline configuration delegate. Called after any options already bound from
    /// <c>IConfiguration</c>, so it can override individual properties.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// To bind options from configuration, call
    /// <c>services.Configure&lt;MatrixChannelOptions&gt;(config.GetSection("channels:matrix"))</c>.
    /// The factory is registered with <c>TryAddSingleton</c> so a host that needs custom transport
    /// behaviour can register its own <see cref="IMatrixClientFactory"/> beforehand and keep it.
    /// </remarks>
    public static IServiceCollection AddBotNexusMatrixChannel(
        this IServiceCollection services,
        Action<MatrixChannelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MatrixChannelOptions>();

        if (configure is not null)
            services.Configure(configure);

        services.AddHttpClient();
        services.TryAddSingleton<IMatrixClientFactory, DefaultMatrixClientFactory>();
        services.AddSingleton<IChannelAdapter, MatrixChannelAdapter>();

        return services;
    }
}
