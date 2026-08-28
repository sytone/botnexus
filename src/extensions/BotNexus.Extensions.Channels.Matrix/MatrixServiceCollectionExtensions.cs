using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
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
        // Explicit factory rather than TryAddSingleton<T,TImpl>: the redactor is an OPTIONAL dependency
        // (#3398) and must resolve to null in a host that never registered one, instead of failing
        // activation. GetService, not GetRequiredService, is the load-bearing part.
        services.TryAddSingleton<IMatrixClientFactory>(sp => new DefaultMatrixClientFactory(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetService<ISecretRedactor>()));

        // Durable /sync cursor (#3595). The database lives under the VERIFIED BotNexus home rather
        // than a home path this extension derives for itself: a store that resolves its own home has
        // never been checked against this world's sentinel, which is exactly where the #2836 guard
        // would have a hole. Prefer the container's BotNexusHome when the host registered one, and
        // fall back to the same canonical resolver's static entry point when it did not, so a
        // standalone host still lands in the right place. TryAdd so a host with its own cursor store
        // keeps it.
        services.TryAddSingleton<IMatrixSyncCursorStore>(sp =>
        {
            var home = sp.GetService<BotNexusHome>();
            var dataRoot = home?.DataPath ?? BotNexusHome.ResolveDataPath() ?? BotNexusHome.ResolveHomePath();
            return new SqliteMatrixSyncCursorStore(
                Path.Combine(dataRoot, "data", "matrix-sync-cursor.db"));
        });

        services.AddSingleton<IChannelAdapter, MatrixChannelAdapter>();

        return services;
    }
}
