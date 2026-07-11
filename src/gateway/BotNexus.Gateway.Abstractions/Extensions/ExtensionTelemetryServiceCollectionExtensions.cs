using BotNexus.Persistence.Sqlite.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Host wiring for the extension telemetry seam (#1852). Registers the shared durable
/// <see cref="IUsageTelemetry"/> store (a single SQLite database all consumers share, isolated by
/// namespace) and the <see cref="IExtensionTelemetryFactory"/> that mints per-extension namespaced
/// metrics and usage handles. Call once during host composition; the platform
/// <see cref="BotNexus.Gateway.Telemetry.IMetrics"/> facade must already be registered
/// (via <c>AddBotNexusTelemetry</c>).
/// </summary>
public static class ExtensionTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Adds the shared durable usage-telemetry store and the extension telemetry factory. Both are
    /// singletons: the store so counter increments from every session and extension accumulate into
    /// one database, the factory so extensions resolve a single seam entry point.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="usageDatabasePath">
    /// Absolute path to the shared usage-telemetry SQLite file. The parent directory is created on
    /// first use. Consumers are isolated by namespace within this single file.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddExtensionTelemetry(
        this IServiceCollection services,
        string usageDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(usageDatabasePath);

        // One shared store for all consumers; TryAdd so a host/test that registered its own
        // IUsageTelemetry (e.g. an in-memory fake) wins.
        services.TryAddSingleton<IUsageTelemetry>(_ => new SqliteUsageTelemetryStore(usageDatabasePath));
        services.TryAddSingleton<IExtensionTelemetryFactory, ExtensionTelemetryFactory>();

        return services;
    }
}
