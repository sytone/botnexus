using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Registers the BotNexus telemetry foundation: the <see cref="IMetrics"/> facade and the
/// OpenTelemetry SDK MeterProvider/TracerProvider scoped to the canonical
/// <see cref="BotNexusMeters.Name"/> instrumentation scope.
/// </summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Adds the BotNexus metrics core and OpenTelemetry SDK wiring.
    /// </summary>
    /// <remarks>
    /// Always registers <see cref="IMetrics"/> so call sites resolve regardless of
    /// configuration. When <see cref="TelemetryConfig.Enabled"/> is <see langword="true"/>
    /// (the default), also wires <c>AddOpenTelemetry().WithMetrics(...)</c> subscribed to the
    /// <c>"BotNexus"</c> meter plus AspNetCore and Http instrumentation, and a
    /// <c>WithTracing(...)</c> scaffold subscribed to the <c>"BotNexus"</c> ActivitySource.
    /// No exporter is configured, so there is no OTLP/network egress by default.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">Root configuration; the <c>telemetry</c> section is bound.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddBotNexusTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(TelemetryConfig.SectionName);
        services.Configure<TelemetryConfig>(section);

        var config = section.Get<TelemetryConfig>() ?? new TelemetryConfig();

        // Always register the facade so DI resolution never depends on the enabled flag.
        services.AddSingleton<IMetrics, BotNexusMetrics>();

        // Hot-path metric recorder (PBI3 #1851): a single instance owns every turn/tool/
        // provider/cron/channel/session instrument. Registered unconditionally so the
        // in-Gateway hot-path seams can resolve it regardless of the enabled flag; when
        // telemetry is disabled the instruments simply have no MeterProvider subscribed.
        services.AddSingleton<HotPathMetrics>();

        // Proof-of-life smoke counter (botnexus.host.starts) increments on boot.
        services.AddHostedService<HostStartupMetrics>();

        if (!config.Enabled)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .WithMetrics(builder =>
            {
                builder.AddMeter(BotNexusMeters.Name);
                builder.AddAspNetCoreInstrumentation();
                builder.AddHttpClientInstrumentation();
            })
            .WithTracing(builder =>
            {
                // Tracing scaffold only: subscribe to the canonical source so PBI2+ can
                // start emitting spans without further host wiring. No custom spans yet.
                builder.AddSource(BotNexusMeters.Name);
                builder.AddAspNetCoreInstrumentation();
                builder.AddHttpClientInstrumentation();
            });

        return services;
    }
}
