using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Registers the BotNexus telemetry foundation: the <see cref="IMetrics"/> facade and the
/// OpenTelemetry SDK MeterProvider/TracerProvider scoped to the canonical
/// <see cref="BotNexusMeters.Name"/> instrumentation scope, plus the optional, off-by-default
/// remote OTLP exporter.
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
    ///
    /// A remote exporter is attached only when <c>telemetry:Exporter:Type</c> is set to
    /// <c>otlp</c> or <c>console</c>. The default is <see cref="TelemetryExporterType.None"/>,
    /// which produces zero network egress: no OTLP connection is ever attempted and no
    /// endpoint is shipped by default. This is the off-by-default safety contract.
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

        // Metrics read surface (#1853): the snapshot collector subscribes a MeterListener to the
        // canonical BotNexus scope so the read endpoint can serve current instrument values without
        // an external collector. Registered as a singleton so accumulation spans the process
        // lifetime, and unconditionally (like IMetrics) so the endpoint resolves regardless of the
        // enabled flag. The endpoint contributor is discovered from DI by MapExtensionEndpoints,
        // so the route is wired without any Program.cs edit (disjoint from PR #1920).
        services.AddSingleton<Snapshot.MetricsSnapshotCollector>();
        services.AddHostedService(sp => sp.GetRequiredService<Snapshot.MetricsSnapshotCollector>());
        services.AddSingleton<BotNexus.Gateway.Abstractions.Extensions.IEndpointContributor, Snapshot.TelemetryEndpointContributor>();

        if (!config.Enabled)
        {
            return services;
        }

        var exporter = config.Exporter;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => ConfigureResource(resource, exporter.Resource))
            .WithMetrics(builder =>
            {
                builder.AddMeter(BotNexusMeters.Name);
                builder.AddAspNetCoreInstrumentation();
                builder.AddHttpClientInstrumentation();
                ConfigureMetricsExporter(builder, exporter);
            })
            .WithTracing(builder =>
            {
                // Subscribe to every canonical BotNexus scope so turn / tool-call /
                // provider-invocation spans (and their child spans, e.g. sub-agent spawns)
                // flow through the same TracerProvider. Sub-agent spawns surface as child
                // spans automatically because they are started under the ambient
                // Activity.Current of the parent turn (standard OTel parent linkage).
                builder.AddSource(BotNexusMeters.Name);
                builder.AddSource(GatewaySourceName);
                builder.AddSource(AgentsSourceName);
                builder.AddSource(ProvidersSourceName);
                builder.AddSource(ChannelsSourceName);
                builder.AddAspNetCoreInstrumentation();
                builder.AddHttpClientInstrumentation();
                ConfigureTracingExporter(builder, exporter);
                ConfigureAgent365Exporter(builder, config.Agent365);
            });

        return services;
    }

    /// <summary>
    /// Applies the configured resource attributes (<c>service.name</c>, a stable
    /// <c>service.instance.id</c> so a downstream aggregator can distinguish instances, and an
    /// optional <c>deployment.environment</c>) to the OpenTelemetry resource.
    /// </summary>
    private static void ConfigureResource(ResourceBuilder resource, ResourceAttributesConfig attrs)
    {
        resource.AddService(
            serviceName: string.IsNullOrWhiteSpace(attrs.ServiceName)
                ? ResourceAttributesConfig.DefaultServiceName
                : attrs.ServiceName,
            serviceInstanceId: attrs.ResolveServiceInstanceId());

        if (!string.IsNullOrWhiteSpace(attrs.DeploymentEnvironment))
        {
            resource.AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment", attrs.DeploymentEnvironment!),
            });
        }
    }

    private static void ConfigureMetricsExporter(MeterProviderBuilder builder, ExporterConfig exporter)
    {
        switch (exporter.Type)
        {
            case TelemetryExporterType.Otlp:
                builder.AddOtlpExporter(options => ConfigureOtlp(options, exporter));
                break;
            case TelemetryExporterType.Console:
                builder.AddConsoleExporter();
                break;
            case TelemetryExporterType.None:
            default:
                // No exporter: zero egress. In-process instruments are still recorded and
                // readable via the local snapshot endpoint.
                break;
        }
    }

    private static void ConfigureTracingExporter(TracerProviderBuilder builder, ExporterConfig exporter)
    {
        switch (exporter.Type)
        {
            case TelemetryExporterType.Otlp:
                builder.AddOtlpExporter(options => ConfigureOtlp(options, exporter));
                break;
            case TelemetryExporterType.Console:
                builder.AddConsoleExporter();
                break;
            case TelemetryExporterType.None:
            default:
                break;
        }
    }

    /// <summary>The Gateway host ActivitySource scope name.</summary>
    internal const string GatewaySourceName = "BotNexus.Gateway";

    /// <summary>The agent-core ActivitySource scope name (turn spans, sub-agent spawns).</summary>
    internal const string AgentsSourceName = "BotNexus.Agents";

    /// <summary>The provider ActivitySource scope name (provider-invocation spans).</summary>
    internal const string ProvidersSourceName = "BotNexus.Providers";

    /// <summary>The channel ActivitySource scope name.</summary>
    internal const string ChannelsSourceName = "BotNexus.Channels";

    /// <summary>
    /// Attaches a second, independent OTLP/HTTP exporter target pointing at the Agent 365
    /// observability ingestion endpoint when <see cref="Agent365ObservabilityConfig.Enabled"/>
    /// is set and an endpoint is configured. This is a raw OTLP export (no A365 SDK dependency):
    /// the same canonical BotNexus spans are shipped to Agent 365 alongside (not instead of) any
    /// generic collector target. Off by default: absent an endpoint, nothing is wired and there
    /// is zero egress to Agent 365.
    /// </summary>
    private static void ConfigureAgent365Exporter(TracerProviderBuilder builder, Agent365ObservabilityConfig agent365)
    {
        if (!agent365.Enabled ||
            string.IsNullOrWhiteSpace(agent365.Endpoint) ||
            !Uri.TryCreate(agent365.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return;
        }

        builder.AddOtlpExporter("agent365", options =>
        {
            options.Endpoint = endpoint;
            // Agent 365 ingests OTLP over HTTP; the direct-OTel route is an HTTP+JSON/protobuf
            // POST to the traces endpoint. Force HttpProtobuf so we never attempt gRPC against
            // the HTTP-only ingestion surface.
            options.Protocol = OtlpExportProtocol.HttpProtobuf;

            var headers = agent365.ResolveHeaders();
            if (headers.Count > 0)
            {
                // OTLP header wire format: comma-separated key=value pairs. These values
                // (bearer tokens etc.) are secrets and are never logged here.
                options.Headers = string.Join(",", headers.Select(h => $"{h.Key}={h.Value}"));
            }
        });
    }

    /// <summary>
    /// Translates the OTel-free <see cref="ExporterConfig"/> into OTLP exporter options:
    /// endpoint, wire protocol, and secret headers. Headers are passed through in the
    /// OpenTelemetry <c>key=value,key2=value2</c> header format.
    /// </summary>
    private static void ConfigureOtlp(OtlpExporterOptions options, ExporterConfig exporter)
    {
        if (!string.IsNullOrWhiteSpace(exporter.Endpoint) &&
            Uri.TryCreate(exporter.Endpoint, UriKind.Absolute, out var endpoint))
        {
            options.Endpoint = endpoint;
        }

        options.Protocol = string.Equals(exporter.Protocol, ExporterConfig.ProtocolHttpProtobuf, StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

        if (exporter.Headers.Count > 0)
        {
            // OTLP header wire format is a comma-separated list of key=value pairs. These
            // values are secrets (e.g. collector auth tokens) and are never logged here.
            options.Headers = string.Join(",", exporter.Headers.Select(h => $"{h.Key}={h.Value}"));
        }
    }
}
