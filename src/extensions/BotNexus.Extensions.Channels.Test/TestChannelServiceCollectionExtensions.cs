using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// Dependency-injection extensions for the opt-in test channel.
/// </summary>
/// <remarks>
/// Two entry points, deliberately separate:
/// <list type="bullet">
///   <item><description>
///     <see cref="AddBotNexusTestChannelSupport"/> registers only the SUPPORT services (options,
///     log capture, logger provider). The dynamic extension loader discovers the adapter and the
///     endpoint contributor itself by contract, so a service contributor must not register them a
///     second time — that would start two adapters for one channel key.
///   </description></item>
///   <item><description>
///     <see cref="AddBotNexusTestChannel"/> additionally registers the adapter and endpoint
///     contributor, for an in-process host that composes the channel directly instead of loading
///     the extension from disk.
///   </description></item>
/// </list>
/// Neither is called from any production composition root; the shipped manifest is disabled, so
/// the dynamic loader also never reaches the adapter unless a configuration explicitly opts in.
/// </remarks>
public static class TestChannelServiceCollectionExtensions
{
    /// <summary>
    /// Registers the support services the test channel needs: bound options, the shared log-capture
    /// buffer, and the additive <see cref="ILoggerProvider"/> that fills it.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">Optional inline configuration (channel key, display name, log bound).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddBotNexusTestChannelSupport(
        this IServiceCollection services,
        Action<TestChannelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TestChannelOptions>();
        if (configure is not null)
            services.Configure(configure);

        // The capture buffer is a singleton shared by the logger provider (writer) and the HTTP
        // endpoints (reader). Its bound is read from the SAME options instance the adapter uses so
        // the manifest's maxCapturedLogEntries is not silently ignored.
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetService<IOptions<TestChannelOptions>>()?.Value ?? new TestChannelOptions();
            return new TestChannelLogCapture(options.MaxCapturedLogEntries);
        });

        // Appended, never substituted: the host's console/file providers keep working while the
        // gateway is under test.
        //
        // Registered by IMPLEMENTATION TYPE, not by factory. TryAddEnumerable REJECTS a factory
        // descriptor for a contract that already has registrations - it cannot tell one anonymous
        // factory from another, so it throws "indistinguishable from other services registered for
        // ILoggerProvider" rather than risk a duplicate. Naming the concrete type gives it the
        // identity it needs, and the container can activate it because TestChannelLogCapture is
        // registered above.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, TestChannelLoggerProvider>());

        return services;
    }

    /// <summary>
    /// Registers the full test channel — support services plus the adapter and its HTTP surface —
    /// for a host that composes the channel in-process rather than loading the extension from disk.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">Optional inline configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddBotNexusTestChannel(
        this IServiceCollection services,
        Action<TestChannelOptions>? configure = null)
    {
        services.AddBotNexusTestChannelSupport(configure);
        services.AddSingleton<IChannelAdapter, TestChannelAdapter>();
        services.AddSingleton<BotNexus.Gateway.Abstractions.Extensions.IEndpointContributor, TestChannelEndpointContributor>();
        return services;
    }
}
